using ClearPay.Application.Payments;
using ClearPay.Application.Ports;
using ClearPay.Application.Wallets;
using ClearPay.Domain.Ledger;
using ClearPay.Domain.Payments;
using ClearPay.Infrastructure.Payments;
using ClearPay.Infrastructure.Payments.Psp;
using ClearPay.Infrastructure.Persistence;
using ClearPay.Messaging;
using ClearPay.Operations.Consumers;
using ClearPay.Operations.Data;
using ClearPay.Operations.Notifications;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPay.Operations.Tests.Support;

public sealed class SqlServerFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "CLEARPAY_TEST_SQLSERVER";

    public SqlServerFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"SQL Server integration test: set {EnvironmentVariable} to run it.";
    }
}

public sealed class RabbitFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "CLEARPAY_TEST_RABBITMQ";

    public RabbitFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"RabbitMQ integration test: set {EnvironmentVariable} to run it.";
    }
}

internal sealed class FixedClock : IClock
{
    public DateTimeOffset UtcNow { get; set; } = new(2026, 11, 9, 10, 0, 0, TimeSpan.Zero);
}

internal sealed class FixedTime : TimeProvider
{
    private readonly FixedClock _clock;

    public FixedTime(FixedClock clock) => _clock = clock;

    public override DateTimeOffset GetUtcNow() => _clock.UtcNow;
}

internal sealed class NoCache : IWalletSummaryCache
{
    public Task<WalletSummary?> GetAsync(string userId, CancellationToken cancellationToken = default) => Task.FromResult<WalletSummary?>(null);

    public Task SetAsync(WalletSummary summary, CancellationToken cancellationToken = default) => Task.CompletedTask;

    public Task InvalidateAsync(string userId, CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>
/// The ledger host's side (SQLite): real card payments through the fake PSPs, its outbox, and the PSPs'
/// settlement drop folder. Used only to produce what the Operations service receives in production.
/// </summary>
internal sealed class LedgerSide : IDisposable
{
    private readonly SqliteConnection _sqlite = new("Data Source=:memory:");
    private readonly ServiceProvider _provider;

    public LedgerSide(FixedClock clock, string settlementDirectory)
    {
        Clock = clock;
        _sqlite.Open();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddDbContext<ClearPayDbContext>(o => o.UseSqlite(_sqlite));
        services.AddSingleton<IClock>(clock);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Reconciliation:SettlementDirectory"] = settlementDirectory
        }).Build());
        services.AddSingleton<SimulatedPspCore>();
        services.AddSingleton<IPaymentServiceProvider, SimBankRestPsp>();
        services.AddSingleton<IPaymentServiceProvider, SimBankSoapPsp>();
        services.AddSingleton<IPaymentServiceProvider, SimPayPsp>();
        services.AddSingleton<IPspRouter, PspRouter>();
        services.AddSingleton<SimSettlementFileWriter>();
        _provider = services.BuildServiceProvider();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public FixedClock Clock { get; }

    public SimSettlementFileWriter SettlementWriter => _provider.GetRequiredService<SimSettlementFileWriter>();

    public ClearPayDbContext NewContext() => _provider.CreateScope().ServiceProvider.GetRequiredService<ClearPayDbContext>();

    public async Task<PaymentResult> PayAsync(string user, decimal amount, string provider, string? key = null)
    {
        var sessionId = Guid.NewGuid();
        await using (var db = NewContext())
        {
            db.ThreeDsSessions.Add(new ThreeDsSession
            {
                Id = sessionId, UserId = user, Amount = amount, CardLast4 = "0010", Status = ThreeDsStatus.Authenticated,
                Eci = "05", AuthenticationValue = "test-cavv", CorrelationId = Guid.NewGuid(),
                CreatedAt = Clock.UtcNow, UpdatedAt = Clock.UtcNow, ExpiresAt = Clock.UtcNow.AddMinutes(10)
            });
            await db.SaveChangesAsync();
        }

        return await CreateAsync(user, sessionId, provider, key ?? Guid.NewGuid().ToString("N"));
    }

    public async Task<PaymentResult> CreateAsync(string user, Guid sessionId, string provider, string key)
    {
        await using var db = NewContext();
        return await Service(db).CreateAsync(new CreateCardPaymentCommand(user, sessionId, provider, true, key));
    }

    public async Task<PaymentResult> RefundAsync(string user, Guid paymentId, decimal amount)
    {
        await using var db = NewContext();
        return await Service(db).RefundAsync(new PaymentActionCommand(user, paymentId, amount, Guid.NewGuid().ToString("N")));
    }

    /// <summary>Outbox rows as the broker would deliver them (MessageId = row id).</summary>
    public async Task<IReadOnlyList<InboundMessage>> OutboxAsync(string? type = null)
    {
        await using var db = NewContext();
        var rows = await db.OutboxMessages.AsNoTracking().ToListAsync();
        return rows.Where(r => type is null || r.Type == type).OrderBy(r => r.OccurredAt)
            .Select(r => new InboundMessage(r.Id, r.Type, r.CorrelationId, r.Payload, 1, false)).ToList();
    }

    private SqlCardPaymentService Service(ClearPayDbContext db) => new(
        db, _provider.GetRequiredService<IPspRouter>(), new SqlIdempotencyStore(db, Clock), Clock, new NoCache(),
        NullLogger<SqlCardPaymentService>.Instance);

    public void Dispose()
    {
        _provider.Dispose();
        _sqlite.Dispose();
    }
}

/// <summary>The Operations service's own side (SQLite): its database, handlers and inbox.</summary>
internal sealed class OperationsSide : IDisposable
{
    private readonly SqliteConnection _sqlite = new("Data Source=:memory:");

    public OperationsSide(FixedClock clock)
    {
        Time = new FixedTime(clock);
        _sqlite.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    public TimeProvider Time { get; }

    public List<Notification> Sent { get; } = new();

    public OperationsDbContext NewContext() =>
        new(new DbContextOptionsBuilder<OperationsDbContext>().UseSqlite(_sqlite).Options);

    public PaymentProjectionHandler Projection(OperationsDbContext db) =>
        new(new InboxProcessor<OperationsDbContext>(db, NullLogger<InboxProcessor<OperationsDbContext>>.Instance), Time);

    public NotificationHandler Notifications(OperationsDbContext db) =>
        new(new InboxProcessor<OperationsDbContext>(db, NullLogger<InboxProcessor<OperationsDbContext>>.Instance),
            new RecordingSender(Sent), Time);

    public void Dispose() => _sqlite.Dispose();

    private sealed class RecordingSender : INotificationSender
    {
        private readonly List<Notification> _sent;

        public RecordingSender(List<Notification> sent) => _sent = sent;

        public Task SendAsync(Notification notification, CancellationToken cancellationToken)
        {
            lock (_sent)
                _sent.Add(notification);
            return Task.CompletedTask;
        }
    }
}
