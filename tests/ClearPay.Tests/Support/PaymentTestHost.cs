using ClearPay.Application.Banking;
using ClearPay.Application.Payments;
using ClearPay.Application.Ports;
using ClearPay.Domain.Ledger;
using ClearPay.Domain.Payments;
using ClearPay.Infrastructure.Payments;
using ClearPay.Infrastructure.Payments.Psp;
using ClearPay.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPay.Tests.Support;

/// <summary>
/// Payment service + the three fake PSPs on one ledger database (SQLite in memory by default,
/// or a SQL Server connection string). The PSP simulators get their own DbContext scope as in production.
/// </summary>
internal sealed class PaymentTestHost : IDisposable
{
    private readonly SqliteConnection? _sqlite;
    private readonly ServiceProvider _provider;

    public PaymentTestHost(string? sqlServer = null)
    {
        var services = new ServiceCollection();
        if (sqlServer is null)
        {
            _sqlite = new SqliteConnection("Data Source=:memory:");
            _sqlite.Open();
            services.AddDbContext<ClearPayDbContext>(o => o.UseSqlite(_sqlite));
        }
        else
        {
            services.AddDbContext<ClearPayDbContext>(o => o.UseSqlServer(sqlServer));
        }

        services.AddSingleton<IClock>(Clock);
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddSingleton<SimulatedPspCore>();
        services.AddSingleton<IPaymentServiceProvider, SimBankRestPsp>();
        services.AddSingleton<IPaymentServiceProvider, SimBankSoapPsp>();
        services.AddSingleton<IPaymentServiceProvider, SimPayPsp>();
        services.AddSingleton<IPspRouter, PspRouter>();
        _provider = services.BuildServiceProvider();

        if (_sqlite is not null)
        {
            using var db = NewContext();
            db.Database.EnsureCreated();
        }
    }

    public TestClock Clock { get; } = new() { UtcNow = new DateTimeOffset(2026, 11, 9, 10, 0, 0, TimeSpan.Zero) };

    public ClearPayDbContext NewContext() => _provider.CreateScope().ServiceProvider.GetRequiredService<ClearPayDbContext>();

    public SqlCardPaymentService NewService(ClearPayDbContext db) => new(
        db,
        _provider.GetRequiredService<IPspRouter>(),
        new SqlIdempotencyStore(db, Clock),
        Clock,
        new NullWalletCache(),
        NullLogger<SqlCardPaymentService>.Instance);

    /// <summary>An Authenticated 3-D Secure session as the ACS callback would leave it.</summary>
    public async Task<Guid> AuthenticatedSessionAsync(string userId, decimal amount, string last4 = "0010")
    {
        await using var db = NewContext();
        var session = new ThreeDsSession
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            Amount = amount,
            CardLast4 = last4,
            Status = ThreeDsStatus.Authenticated,
            Eci = "05",
            AuthenticationValue = "test-cavv",
            AcsNonce = Guid.NewGuid().ToString("N"),
            CorrelationId = Guid.NewGuid(),
            CreatedAt = Clock.UtcNow,
            UpdatedAt = Clock.UtcNow,
            ExpiresAt = Clock.UtcNow.AddMinutes(10)
        };
        db.ThreeDsSessions.Add(session);
        await db.SaveChangesAsync();
        return session.Id;
    }

    public async Task<decimal> NetAsync(string userId)
    {
        await using var db = NewContext();
        var wallet = await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.UserId == userId);
        if (wallet is null)
            return 0m;
        var amounts = await db.LedgerEntries.AsNoTracking().Where(e => e.WalletId == wallet.Id).Select(e => e.Amount).ToListAsync();
        return amounts.Sum();
    }

    /// <summary>Moves money out of the customer's wallet (as a withdrawal would) to set up low-balance cases.</summary>
    public async Task SpendAsync(string userId, decimal amount)
    {
        await using var db = NewContext();
        var customer = await db.Wallets.SingleAsync(w => w.UserId == userId);
        var treasury = await db.Wallets.SingleAsync(w => w.UserId == Treasury.UserId);
        var (debit, credit) = LedgerPair.Create(customer.Id, treasury.Id, amount, Guid.NewGuid(), LedgerEntryKind.Withdraw, at: Clock.UtcNow);
        db.LedgerEntries.AddRange(debit, credit);
        await db.SaveChangesAsync();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _sqlite?.Dispose();
    }
}
