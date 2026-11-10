using ClearPay.Application.Reconciliation;
using FluentAssertions;

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

