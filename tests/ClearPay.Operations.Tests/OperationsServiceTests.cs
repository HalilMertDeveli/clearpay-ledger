using System.Text.Json;
using ClearPay.Application.Payments;
using ClearPay.Application.Ports;
using ClearPay.Infrastructure.Payments.Psp;
using ClearPay.Messaging;
using ClearPay.Operations.Consumers;
using ClearPay.Operations.Reconciliation;
using ClearPay.Operations.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPay.Operations.Tests;

public sealed class NotificationAndProjectionTests : IDisposable
{
    private readonly FixedClock _clock = new();
    private readonly OperationsSide _ops;

    public NotificationAndProjectionTests()
    {
        _ops = new OperationsSide(_clock);
    }

    [Fact]
    public async Task Captured_payment_notifies_the_customer_once_even_when_redelivered()
    {
        var message = Event("clearpay.payment.captured", new { paymentId = Guid.NewGuid(), userId = "u-1", amount = 120.5m, cardLast4 = "0010" });

        await using (var db = _ops.NewContext())
            (await _ops.Notifications(db).HandleAsync(message, CancellationToken.None)).Should().BeTrue();
        await using (var db = _ops.NewContext())
            (await _ops.Notifications(db).HandleAsync(message with { Redelivered = true }, CancellationToken.None)).Should().BeFalse();

        await using var check = _ops.NewContext();
        var row = await check.Notifications.SingleAsync();
        row.Recipient.Should().Be("u-1");
        row.Template.Should().Be("card-payment-captured");
        row.Text.Should().Be("120.50 TRY from your card ending 0010 was added to your wallet.");
        _ops.Sent.Should().ContainSingle();
    }

    [Fact]
    public async Task Reconciliation_report_goes_to_the_operations_team_and_unknown_events_are_ignored()
    {
        var report = Event("clearpay.reconciliation.completed",
            new { runId = Guid.NewGuid(), businessDate = "2026-11-08", matched = 3, mismatches = 4 });
        await using (var db = _ops.NewContext())
            (await _ops.Notifications(db).HandleAsync(report, CancellationToken.None)).Should().BeTrue();
        await using (var db = _ops.NewContext())
            (await _ops.Notifications(db).HandleAsync(Event("clearpay.something.else", new { }), CancellationToken.None)).Should().BeFalse();

        await using var check = _ops.NewContext();
        (await check.Notifications.SingleAsync()).Text.Should().StartWith("Reconciliation 2026-11-08: 3 matched, 4 mismatches");
    }

    [Fact]
    public async Task Projection_keeps_the_final_state_when_snapshots_arrive_out_of_order()
    {
        var operationId = Guid.NewGuid();
        object Snapshot(string status, string? reference) => new
        {
            operationId, paymentId = Guid.NewGuid(), userId = "u-2", type = "Capture", status, provider = "simpay",
            providerReference = reference, merchantReference = Guid.NewGuid(), amount = 10m,
            createdAt = _clock.UtcNow, updatedAt = _clock.UtcNow
        };

        foreach (var (status, reference) in new[] { ("Succeeded", "SIMPAY-CAPTURE-1"), ("Pending", (string?)null) })
        {
            await using var db = _ops.NewContext();
            await _ops.Projection(db).HandleAsync(Event("clearpay.payment.operation.recorded", Snapshot(status, reference)), CancellationToken.None);
        }

        await using var check = _ops.NewContext();
        var row = await check.PaymentOperations.SingleAsync();
        row.Status.Should().Be("Succeeded");
        row.ProviderReference.Should().Be("SIMPAY-CAPTURE-1");
    }

    [Fact]
    public async Task Unreadable_events_are_poison()
    {
        await using var db = _ops.NewContext();
        var bad = new InboundMessage(Guid.NewGuid(), "clearpay.payment.operation.recorded", Guid.NewGuid(), "{\"x\":1}", 1, false);
        await FluentActions.Awaiting(() => _ops.Projection(db).HandleAsync(bad, CancellationToken.None))
            .Should().ThrowAsync<PoisonMessageException>();
        var badNote = new InboundMessage(Guid.NewGuid(), "clearpay.payment.captured", Guid.NewGuid(), "{\"amount\":1}", 1, false);
        await FluentActions.Awaiting(() => _ops.Notifications(db).HandleAsync(badNote, CancellationToken.None))
            .Should().ThrowAsync<PoisonMessageException>();
    }

    private static InboundMessage Event(string type, object payload) =>
        new(Guid.NewGuid(), type, Guid.NewGuid(), JsonSerializer.Serialize(payload), 1, false);

    public void Dispose() => _ops.Dispose();
}

/// <summary>
/// Ledger host produces payments, events and PSP files; the Operations service reconciles from its own
/// projection and the drop folder. Events are handed over in process here; the RabbitMQ test covers the broker leg.
/// </summary>
public sealed class ReconciliationAcrossServicesTests : IDisposable
{
    private const string User = "recon-user";
    private readonly FixedClock _clock = new();
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "clearpay-ops-settlements-" + Guid.NewGuid().ToString("N"));
    private readonly LedgerSide _ledger;
    private readonly OperationsSide _ops;

    public ReconciliationAcrossServicesTests()
    {
        _ledger = new LedgerSide(_clock, _dir);
        _ops = new OperationsSide(_clock);
    }

    [Fact]
    public async Task Nightly_run_finds_every_kind_of_mismatch_without_reading_the_ledger_database()
    {
        var day = DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime);
        var ok = await _ledger.PayAsync(User, 100m, SimBankRestPsp.ProviderName);
        (await _ledger.RefundAsync(User, ok.Payment!.PaymentId, 30m)).Kind.Should().Be(PaymentResultKind.Ok);
        await _ledger.PayAsync(User, 40m, SimBankSoapPsp.ProviderName);
        await _ledger.PayAsync(User, 10.77m, SimBankRestPsp.ProviderName); // left out of the PSP file
        await _ledger.PayAsync(User, 10.78m, SimBankSoapPsp.ProviderName); // settled one kuruş higher

        // simpay x.99: the second call leaves the capture Pending (its response was lost).
        var lost = await _ledger.PayAsync(User, 10.99m, SimPayPsp.ProviderName, "lost");
        lost.Kind.Should().Be(PaymentResultKind.TimedOut);
        await using (var db = _ledger.NewContext())
        {
            var sessionId = (await db.CardPayments.AsNoTracking().SingleAsync(p => p.Amount == 10.99m)).ThreeDsSessionId;
            (await _ledger.CreateAsync(User, sessionId, SimPayPsp.ProviderName, "lost")).Kind.Should().Be(PaymentResultKind.TimedOut);
        }

        // A capture the PSP settled that the ledger never asked for.
        await using (var db = _ledger.NewContext())
        {
            db.SimPspTransactions.Add(new Domain.Payments.SimPspTransaction
            {
                Id = Guid.NewGuid(), Provider = SimPayPsp.ProviderName, IdempotencyKey = "foreign",
                Type = Domain.Payments.PaymentOperationType.Capture, Reference = "SIMPAY-CAPTURE-FOREIGN", Amount = 5m,
                Approved = true, CorrelationId = Guid.NewGuid(), CreatedAt = _clock.UtcNow
            });
            await db.SaveChangesAsync();
        }

        // Ledger -> Operations: the operation events (as RabbitMQ would deliver them), and the PSPs' files.
        foreach (var message in await _ledger.OutboxAsync("clearpay.payment.operation.recorded"))
        {
            await using var db = _ops.NewContext();
            await _ops.Projection(db).HandleAsync(message, CancellationToken.None);
        }

        (await _ledger.SettlementWriter.WriteAsync(day)).Should().HaveCount(3);

        var summary = await RunAsync(day, force: false);

        summary.Status.Should().Be("Completed");
        await using var check = _ops.NewContext();
        var kinds = await check.ReconciliationItems.Where(i => i.RunId == summary.RunId)
            .GroupBy(i => i.Kind).Select(g => new { g.Key, Count = g.Count() }).ToDictionaryAsync(g => g.Key, g => g.Count);
        kinds.Should().BeEquivalentTo(new Dictionary<string, int>
        {
            ["Matched"] = 3,
            ["MissingInSettlement"] = 1,
            ["AmountMismatch"] = 1,
            ["PendingConfirmedBySettlement"] = 1,
            ["MissingInLedger"] = 1
        });
        summary.LedgerTotal.Should().Be(100m - 30m + 40m + 10.77m + 10.78m + 10.99m);
        summary.SettlementTotal.Should().Be(100m - 30m + 40m + 10.79m + 10.99m + 5m);

        (await RunAsync(day, force: false)).Skipped.Should().BeTrue();
        (await RunAsync(day, force: true)).RunId.Should().NotBe(summary.RunId);
    }

    [Fact]
    public async Task A_missing_settlement_file_is_reported_not_guessed()
    {
        var summary = await RunAsync(new DateOnly(2026, 1, 1), force: false);
        summary.Status.Should().Be("Completed");
        await using var check = _ops.NewContext();
        (await check.ReconciliationRuns.SingleAsync()).Error.Should().Contain("simbank-rest: no settlement file");
    }

    [Fact]
    public async Task Nightly_job_reconciles_yesterday()
    {
        var calls = new List<(DateOnly, bool)>();
        var service = new RecordingService(calls);
        _clock.UtcNow = new DateTimeOffset(2026, 11, 9, 2, 30, 0, TimeSpan.Zero);

        await new ReconciliationJob(service, _ops.Time).Run();

        calls.Should().ContainSingle().Which.Should().Be((new DateOnly(2026, 11, 8), false));
    }

    private async Task<ReconciliationRunSummary> RunAsync(DateOnly day, bool force)
    {
        await using var db = _ops.NewContext();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Reconciliation:SettlementDirectory"] = _dir
        }).Build();
        var service = new OperationsReconciliationService(db, new FileSystemSettlementFileSource(config),
            new ProjectionSettlementSource(db), _ops.Time, new RabbitOptions { Uri = "" },
            new ServiceCollection().BuildServiceProvider(), NullLogger<OperationsReconciliationService>.Instance);
        return await service.RunAsync(day, force);
    }

    public void Dispose()
    {
        _ledger.Dispose();
        _ops.Dispose();
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private sealed class RecordingService : IReconciliationService
    {
        private readonly List<(DateOnly, bool)> _calls;

        public RecordingService(List<(DateOnly, bool)> calls) => _calls = calls;

        public Task<ReconciliationRunSummary> RunAsync(DateOnly businessDate, bool force, CancellationToken cancellationToken = default)
        {
            _calls.Add((businessDate, force));
            return Task.FromResult(new ReconciliationRunSummary(Guid.NewGuid(), businessDate, "Completed", 0, 0, 0, 0,
                DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch, false));
        }

        public Task<IReadOnlyList<ReconciliationRunSummary>> ListAsync(int take, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<(ReconciliationRunSummary Run, IReadOnlyList<ReconciliationItemView> Items)?> GetAsync(
            Guid runId, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
