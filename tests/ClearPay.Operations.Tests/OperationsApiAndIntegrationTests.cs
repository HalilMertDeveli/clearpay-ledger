using RabbitMQ.Client;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using ClearPay.Application.Payments;
using ClearPay.Infrastructure.Messaging;
using ClearPay.Infrastructure.Payments.Psp;
using ClearPay.Infrastructure.Persistence;
using ClearPay.Messaging;
using ClearPay.Operations.Consumers;
using ClearPay.Operations.Data;
using ClearPay.Operations.Notifications;
using ClearPay.Operations.Reconciliation;
using ClearPay.Operations.Tests.Support;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.SqlClient;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.IdentityModel.Tokens;

namespace ClearPay.Operations.Tests;

public sealed class OperationsApiTests : IClassFixture<OperationsApiTests.Factory>
{
    public const string Key = "test-only-jwt-signing-key-not-a-secret-0123456789";
    private readonly Factory _factory;

    public OperationsApiTests(Factory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Reports_need_an_admin_token_from_the_ledger_hosts_issuer()
    {
        var client = _factory.CreateClient();
        (await client.GetAsync("/health")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.GetAsync("/api/reconciliation/runs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("Customer"));
        (await client.GetAsync("/api/reconciliation/runs")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("Admin"));
        var run = await client.PostAsync("/api/reconciliation/runs?date=2026-11-08", null);
        run.StatusCode.Should().Be(HttpStatusCode.Created);
        (await client.GetStringAsync("/api/reconciliation/runs")).Should().Contain("2026-11-08");
        (await client.GetAsync("/api/notifications?recipient=operations-team")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await client.PostAsync("/api/reconciliation/runs?date=08.11.2026", null)).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token("Admin", key: Key.Replace('0', '1')));
        (await client.GetAsync("/api/reconciliation/runs")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    private static string Token(string role, string key = Key)
    {
        var creds = new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(key)), SecurityAlgorithms.HmacSha256);
        var jwt = new JwtSecurityToken("ClearPay", "ClearPay",
            new[] { new Claim(ClaimTypes.NameIdentifier, "ops-test"), new Claim(ClaimTypes.Role, role) },
            expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(jwt);
    }

    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            var db = Path.Combine(Path.GetTempPath(), $"clearpay-ops-{Guid.NewGuid():N}.db");
            builder.UseEnvironment("Development");
            builder.UseSetting("ConnectionStrings:Operations", $"Data Source={db}");
            builder.UseSetting("Operations:UseSqlite", "true");
            builder.UseSetting("ConnectionStrings:RabbitMq", "");
            builder.UseSetting("Hangfire:Enabled", "false");
            builder.UseSetting("Jwt:SigningKey", Key);
            builder.UseSetting("Reconciliation:SettlementDirectory", Path.Combine(Path.GetTempPath(), $"clearpay-ops-files-{Guid.NewGuid():N}"));
        }
    }
}

/// <summary>Real broker between two separate hosts: ledger outbox -> RabbitMQ -> Operations consumers.</summary>
public sealed class CrossServiceRabbitTests
{
    [RabbitFact]
    public async Task Ledger_events_reach_the_operations_service_over_rabbitmq_and_a_republish_notifies_once()
    {
        var prefix = "test-" + Guid.NewGuid().ToString("N")[..8] + ".";
        var dir = Path.Combine(Path.GetTempPath(), "clearpay-xsvc-" + Guid.NewGuid().ToString("N"));
        var clock = new FixedClock { UtcNow = DateTimeOffset.UtcNow };
        var options = new RabbitOptions
        {
            Uri = Environment.GetEnvironmentVariable(RabbitFactAttribute.EnvironmentVariable),
            EventsExchange = prefix + "events",
            QueuePrefix = prefix,
            RetryDelays = new[] { TimeSpan.FromMilliseconds(200) },
            MaxAttempts = 2
        };

        using var ledger = new LedgerSide(clock, dir);
        using var opsSqlite = new SqliteConnection("Data Source=:memory:");
        opsSqlite.Open();
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddDbContext<OperationsDbContext>(o => o.UseSqlite(opsSqlite));
        services.AddSingleton<TimeProvider>(new FixedTime(clock));
        services.AddSingleton(options);
        services.AddSingleton<RabbitConnectionProvider>();
        services.AddSingleton<RabbitEventPublisher>();
        services.AddScoped(typeof(InboxProcessor<>));
        services.AddScoped<PaymentProjectionHandler>();
        services.AddScoped<NotificationHandler>();
        services.AddSingleton<INotificationSender, LogNotificationSender>();
        services.AddSingleton(new MessageHandlerRegistry().Add<PaymentProjectionHandler>().Add<NotificationHandler>());
        services.AddSingleton<RabbitConsumerService>();
        await using var ops = services.BuildServiceProvider();
        using (var scope = ops.CreateScope())
            scope.ServiceProvider.GetRequiredService<OperationsDbContext>().Database.EnsureCreated();

        var consumers = ops.GetRequiredService<RabbitConsumerService>();
        await consumers.StartAsync(CancellationToken.None);
        await consumers.Started.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var paid = await ledger.PayAsync("xsvc-user", 64m, SimBankRestPsp.ProviderName);
            paid.Kind.Should().Be(PaymentResultKind.Ok);

            // The ledger host's own outbox worker publishes through its own connection.
            using var ledgerBroker = new RabbitConnectionProvider(options, NullLogger<RabbitConnectionProvider>.Instance);
            var publisher = new RabbitOutboxPublisher(new RabbitEventPublisher(ledgerBroker, options, NullLogger<RabbitEventPublisher>.Instance));
            await using (var db = ledger.NewContext())
                (await new SqlOutboxProcessor(db, publisher, ledger.Clock).ProcessPendingAsync()).Should().BeGreaterThan(0);

            await WaitAsync(async () =>
            {
                await using var db = ops.CreateScope().ServiceProvider.GetRequiredService<OperationsDbContext>();
                return await db.Notifications.AnyAsync() && await db.PaymentOperations.CountAsync(p => p.Status == "Succeeded") == 2;
            });

            // At-least-once: the ledger publishes every row again (crash before marking them Sent).
            await using (var db = ledger.NewContext())
            {
                await db.OutboxMessages.ExecuteUpdateAsync(s => s.SetProperty(m => m.Status, Domain.Ledger.OutboxStatus.Pending));
                await new SqlOutboxProcessor(db, publisher, ledger.Clock).ProcessPendingAsync();
            }

            await Task.Delay(1500);
            await using var check = ops.CreateScope().ServiceProvider.GetRequiredService<OperationsDbContext>();
            var notes = await check.Notifications.ToListAsync();
            notes.Should().ContainSingle().Which.Text.Should().Contain("64.00 TRY");
            (await check.PaymentOperations.CountAsync()).Should().Be(2, "authorize + capture, one row each");
        }
        finally
        {
            await consumers.StopAsync(CancellationToken.None);
            consumers.Dispose();
            using var channel = ops.GetRequiredService<RabbitConnectionProvider>().Get().CreateModel();
            foreach (var q in new[] { PaymentProjectionHandler.ConsumerName, NotificationHandler.ConsumerName })
            {
                channel.QueueDelete(prefix + q);
                channel.QueueDelete(prefix + q + ".dlq");
                channel.QueueDelete(prefix + q + ".retry.1");
            }

            channel.ExchangeDelete(options.EventsExchange);
            if (Directory.Exists(dir))
                Directory.Delete(dir, true);
        }
    }

    private static async Task WaitAsync(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(100);
        }

        throw new TimeoutException("Operations service did not receive the events in time.");
    }
}

/// <summary>The Operations migration and ops.usp_SettlementCandidates on a real SQL Server.</summary>
public sealed class OperationsSqlServerTests
{
    [SqlServerFact]
    public async Task Migration_creates_the_schema_and_the_settlement_procedure_returns_one_day()
    {
        var server = Environment.GetEnvironmentVariable(SqlServerFactAttribute.EnvironmentVariable)!;
        var builder = new SqlConnectionStringBuilder(server) { InitialCatalog = "ClearPayOpsTest_" + Guid.NewGuid().ToString("N")[..12], TrustServerCertificate = true };
        var options = new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlServer(builder.ConnectionString, sql => sql.MigrationsHistoryTable("__EFMigrationsHistory", OperationsDbContext.Schema)).Options;
        try
        {
            await using (var db = new OperationsDbContext(options))
            {
                await db.Database.MigrateAsync();
                var midnight = new DateTimeOffset(2026, 11, 8, 0, 0, 0, TimeSpan.Zero);
                PaymentOperationProjection Row(string type, string status, DateTimeOffset at) => new()
                {
                    OperationId = Guid.NewGuid(), PaymentId = Guid.NewGuid(), UserId = "u", Type = type, Status = status,
                    Provider = "simpay", ProviderReference = "R-" + Guid.NewGuid().ToString("N")[..6], MerchantReference = Guid.NewGuid(),
                    Amount = 1m, CreatedAt = at, UpdatedAt = at
                };
                db.PaymentOperations.AddRange(
                    Row("Capture", "Succeeded", midnight),
                    Row("Refund", "Pending", midnight.AddHours(23.9)),
                    Row("Refund", "Failed", midnight.AddHours(5)),
                    Row("Authorize", "Succeeded", midnight.AddHours(1)),
                    Row("Capture", "Succeeded", midnight.AddDays(1)),
                    Row("Capture", "Succeeded", midnight.AddTicks(-1)));
                await db.SaveChangesAsync();

                var rows = await new ProjectionSettlementSource(db).LoadAsync(new DateOnly(2026, 11, 8));
                rows.Select(r => (r.Type, r.Status)).Should().BeEquivalentTo(new[] { ("Capture", "Succeeded"), ("Refund", "Pending") });
            }
        }
        finally
        {
            SqlConnection.ClearAllPools();
            var master = new SqlConnectionStringBuilder(builder.ConnectionString) { InitialCatalog = "master" };
            await using var connection = new SqlConnection(master.ConnectionString);
            await connection.OpenAsync();
            await using var drop = connection.CreateCommand();
            drop.CommandText = $"IF DB_ID(N'{builder.InitialCatalog}') IS NOT NULL BEGIN ALTER DATABASE [{builder.InitialCatalog}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{builder.InitialCatalog}]; END";
            await drop.ExecuteNonQueryAsync();
        }
    }
}
