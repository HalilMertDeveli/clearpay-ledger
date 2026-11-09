using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using ClearPay.Application.Payments;
using ClearPay.Application.Ports;
using ClearPay.Application.Reconciliation;
using ClearPay.Domain.Identity;
using ClearPay.Domain.Payments;
using ClearPay.Infrastructure.Identity;
using ClearPay.Infrastructure.Jobs;
using ClearPay.Infrastructure.Payments.Psp;
using ClearPay.Infrastructure.Reconciliation;
using ClearPay.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPay.Tests;

public sealed class ReconciliationEngineTests
{
    private static readonly DateOnly Day = new(2026, 11, 8);

    [Fact]
    public void Classifies_every_kind_of_difference()
    {
        var m1 = Guid.NewGuid();
        var m2 = Guid.NewGuid();
        var m3 = Guid.NewGuid();
        var m4 = Guid.NewGuid();
        var ledger = new[]
        {
            new LedgerSideRecord(Guid.NewGuid(), "p", "Capture", "Succeeded", "R1", m1, 100m),
            new LedgerSideRecord(Guid.NewGuid(), "p", "Capture", "Succeeded", "R2", m2, 50m),
            new LedgerSideRecord(Guid.NewGuid(), "p", "Refund", "Succeeded", "R3", m3, 10m),
            new LedgerSideRecord(Guid.NewGuid(), "p", "Capture", "Pending", null, m4, 20m),
            new LedgerSideRecord(Guid.NewGuid(), "p", "Refund", "Pending", null, Guid.NewGuid(), 5m)
        };
        var settlement = new[]
        {
            new SettlementLine("p", Day, "Capture", "R1", m1, 100m, "TRY"),
            new SettlementLine("p", Day, "Refund", "R3", m3, 10.01m, "TRY"),
            new SettlementLine("p", Day, "Capture", "R9", m4, 20m, "TRY"),
            new SettlementLine("p", Day, "Capture", "R-unknown", Guid.NewGuid(), 7m, "TRY")
        };

        var items = ReconciliationEngine.Match(ledger, settlement);

        items.Select(i => i.Kind).Should().BeEquivalentTo(new[]
        {
            ReconciliationItemKind.Matched,
            ReconciliationItemKind.MissingInSettlement,
            ReconciliationItemKind.AmountMismatch,
            ReconciliationItemKind.PendingConfirmedBySettlement,
            ReconciliationItemKind.PendingNotInSettlement,
            ReconciliationItemKind.MissingInLedger
        });
        items.Single(i => i.Kind == ReconciliationItemKind.PendingConfirmedBySettlement).Reference.Should().Be("R9");
    }

    [Fact]
    public void Same_reference_at_another_provider_does_not_match()
    {
        var m = Guid.NewGuid();
        var items = ReconciliationEngine.Match(
            new[] { new LedgerSideRecord(Guid.NewGuid(), "a", "Capture", "Succeeded", "R1", m, 1m) },
            new[] { new SettlementLine("b", Day, "Capture", "R1", m, 1m, "TRY") });

        items.Select(i => i.Kind).Should().BeEquivalentTo(new[]
        {
            ReconciliationItemKind.MissingInSettlement, ReconciliationItemKind.MissingInLedger
        });
    }

    [Fact]
    public void Settlement_csv_round_trips_and_reports_bad_lines()
    {
        var line = new SettlementLine("simpay", Day, "Refund", "SIMPAY-REFUND-1", Guid.NewGuid(), 12.30m, "TRY");
        var csv = SettlementCsv.Write(new[] { line });

        var (lines, errors) = SettlementCsv.Parse(csv);
        lines.Should().ContainSingle().Which.Should().Be(line);
        errors.Should().BeEmpty();

        var (broken, brokenErrors) = SettlementCsv.Parse(csv + "simpay,2026-13-01,Refund,X,nope,1,TRY\n");
        broken.Should().HaveCount(1);
        brokenErrors.Should().ContainSingle().Which.Should().Contain("Line 3");

        SettlementCsv.Parse("wrong,header\n").Errors.Should().ContainSingle();
    }
}

/// <summary>Real payments through the fake PSPs, real settlement files on disk, real matching.</summary>
public sealed class ReconciliationServiceTests : IDisposable
{
    private const string User = "recon-user";
    private readonly PaymentTestHost _host = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clearpay-settlements-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Nightly_run_records_matches_and_every_kind_of_mismatch()
    {
        var day = DateOnly.FromDateTime(_host.Clock.UtcNow.UtcDateTime);
        var ok = await PayAsync(100m, SimBankRestPsp.ProviderName);
        await RefundAsync(ok, 30m);
        await PayAsync(40m, SimBankSoapPsp.ProviderName);
        await PayAsync(10.77m, SimBankRestPsp.ProviderName); // PSP leaves it out of the file
        await PayAsync(10.78m, SimBankSoapPsp.ProviderName); // PSP settles 10.79

        // simpay x.99: authorization and capture responses are lost; the second call leaves the capture Pending.
        var lostSession = await _host.AuthenticatedSessionAsync(User, 10.99m);
        (await CreateAsync(lostSession, SimPayPsp.ProviderName, "lost")).Kind.Should().Be(PaymentResultKind.TimedOut);
        (await CreateAsync(lostSession, SimPayPsp.ProviderName, "lost")).Kind.Should().Be(PaymentResultKind.TimedOut);

        // A capture the PSP settled that we never asked for.
        await using (var db = _host.NewContext())
        {
            db.SimPspTransactions.Add(new SimPspTransaction
            {
                Id = Guid.NewGuid(), Provider = SimPayPsp.ProviderName, IdempotencyKey = "foreign", Type = PaymentOperationType.Capture,
                Reference = "SIMPAY-CAPTURE-FOREIGN", Amount = 5m, Approved = true, CorrelationId = Guid.NewGuid(),
                CreatedAt = _host.Clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var summary = await RunAsync(day, force: false);

        summary.Status.Should().Be("Completed");
        await using var check = _host.NewContext();
        var kinds = await check.ReconciliationItems.Where(i => i.RunId == summary.RunId)
            .GroupBy(i => i.Kind).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        kinds.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Matched"] = 3,                        // 100 capture, 30 refund, 40 capture
            ["MissingInSettlement"] = 1,            // 10.77
            ["AmountMismatch"] = 1,                 // 10.78 vs 10.79
            ["PendingConfirmedBySettlement"] = 1,   // simpay lost capture response
            ["MissingInLedger"] = 1                 // foreign capture
        });
        summary.Matched.Should().Be(3);
        summary.Mismatches.Should().Be(4);
        summary.LedgerTotal.Should().Be(100m - 30m + 40m + 10.77m + 10.78m + 10.99m);
        summary.SettlementTotal.Should().Be(100m - 30m + 40m + 10.79m + 10.99m + 5m);

        File.Exists(Path.Combine(_dir, $"simbank-rest_{day:yyyyMMdd}.csv")).Should().BeTrue();
        (await check.OutboxMessages.CountAsync(m => m.Type == "clearpay.reconciliation.completed")).Should().Be(1);

        var again = await RunAsync(day, force: false);
        again.Skipped.Should().BeTrue();
        again.RunId.Should().Be(summary.RunId);
        (await RunAsync(day, force: true)).RunId.Should().NotBe(summary.RunId);
    }

    [Fact]
    public async Task Hangfire_job_reconciles_yesterday_once()
    {
        var service = new RecordingService();
        var clock = new TestClock { UtcNow = new DateTimeOffset(2026, 11, 9, 2, 30, 0, TimeSpan.Zero) };

        await new ReconciliationHangfireJob(service, clock).Run();

        service.Calls.Should().ContainSingle().Which.Should().Be((new DateOnly(2026, 11, 8), false));
    }

    private async Task<ReconciliationRunSummary> RunAsync(DateOnly day, bool force)
    {
        await using var db = _host.NewContext();
        var files = new SimSettlementFileSource(
            new SingleContextScopeFactory(_host),
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Reconciliation:SettlementDirectory"] = _dir
            }).Build());
        var service = new SqlReconciliationService(db, files, new SqlLedgerSettlementSource(db), _host.Clock,
            NullLogger<SqlReconciliationService>.Instance);
        return await service.RunAsync(day, force);
    }

    private async Task<Guid> PayAsync(decimal amount, string provider)
    {
        var session = await _host.AuthenticatedSessionAsync(User, amount);
        var result = await CreateAsync(session, provider, Guid.NewGuid().ToString("N"));
        result.Kind.Should().Be(PaymentResultKind.Ok);
        return result.Payment!.PaymentId;
    }

    private async Task RefundAsync(Guid paymentId, decimal amount)
    {
        await using var db = _host.NewContext();
        (await _host.NewService(db).RefundAsync(new PaymentActionCommand(User, paymentId, amount, Guid.NewGuid().ToString("N"))))
            .Kind.Should().Be(PaymentResultKind.Ok);
    }

    private async Task<PaymentResult> CreateAsync(Guid session, string provider, string key)
    {
        await using var db = _host.NewContext();
        return await _host.NewService(db).CreateAsync(new CreateCardPaymentCommand(User, session, provider, true, key));
    }

    public void Dispose()
    {
        _host.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class SingleContextScopeFactory : IServiceScopeFactory
    {
        private readonly PaymentTestHost _host;

        public SingleContextScopeFactory(PaymentTestHost host) => _host = host;

        public IServiceScope CreateScope() => new Scope(_host.NewContext());

        private sealed class Scope : IServiceScope, IServiceProvider
        {
            private readonly Infrastructure.Persistence.ClearPayDbContext _db;

            public Scope(Infrastructure.Persistence.ClearPayDbContext db) => _db = db;

            public IServiceProvider ServiceProvider => this;

            public object? GetService(Type serviceType) =>
                serviceType == typeof(Infrastructure.Persistence.ClearPayDbContext) ? _db : null;

            public void Dispose() => _db.Dispose();
        }
    }

    private sealed class RecordingService : IReconciliationService
    {
        public List<(DateOnly, bool)> Calls { get; } = new();

        public Task<ReconciliationRunSummary> RunAsync(DateOnly businessDate, bool force, CancellationToken cancellationToken = default)
        {
            Calls.Add((businessDate, force));
            return Task.FromResult(new ReconciliationRunSummary(Guid.NewGuid(), businessDate, "Completed", 0, 0, 0, 0,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false));
        }

        public Task<IReadOnlyList<ReconciliationRunSummary>> ListAsync(int take, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(ReconciliationRunSummary Run, IReadOnlyList<ReconciliationItemView> Items)?> GetAsync(
            Guid runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}

public sealed class ReconciliationAdminApiTests : IClassFixture<ClearPayWebFactory>
{
    private readonly ClearPayWebFactory _factory;

    public ReconciliationAdminApiTests(ClearPayWebFactory factory)
    {
        _factory = factory;
    }

    [Fact]
    public async Task Admin_can_run_and_read_a_reconciliation_but_customers_cannot()
    {
        var client = _factory.CreateClient();
        var customer = await ApiSession.RegisterAsync(client, "recon-customer");
        using (var denied = await ApiSession.PostJsonAsync(client, customer.Token, "/api/admin/reconciliation/runs?date=2026-11-08", "{}", null))
            denied.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        var token = await client.PostAsJsonAsync("/api/token", new
        {
            email = IdentitySeeder.DevelopmentAdminEmail,
            password = ClearPayWebFactory.SeedAdminPassword,
            accountKind = AccountKinds.Bireysel
        });
        var jwt = JsonDocument.Parse(await token.Content.ReadAsStringAsync()).RootElement.GetProperty("access_token").GetString();

        using var run = await ApiSession.PostJsonAsync(client, jwt, "/api/admin/reconciliation/runs?date=2026-11-08", "{}", null);
        run.StatusCode.Should().Be(HttpStatusCode.Created);
        var runId = JsonDocument.Parse(await run.Content.ReadAsStringAsync()).RootElement.GetProperty("runId").GetGuid();

        using var detail = await ApiSession.GetAsync(client, jwt, $"/api/admin/reconciliation/runs/{runId}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        (await detail.Content.ReadAsStringAsync()).Should().Contain("\"status\":\"Completed\"");

        using var bad = await ApiSession.PostJsonAsync(client, jwt, "/api/admin/reconciliation/runs?date=08.11.2026", "{}", null);
        bad.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
