using System.Text.Json;
using ClearPay.Application.Ports;
using ClearPay.Application.Reconciliation;
using ClearPay.Domain.Ledger;
using ClearPay.Domain.Payments;
using ClearPay.Domain.Reconciliation;
using ClearPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClearPay.Infrastructure.Reconciliation;

/// <summary>Our side of the comparison: captures and refunds sent to a PSP on the business date.</summary>
public sealed class SqlLedgerSettlementSource : ILedgerSettlementSource
{
    private readonly ClearPayDbContext _db;

    public SqlLedgerSettlementSource(ClearPayDbContext db)
    {
        _db = db;
    }

    /// <summary>SQL Server: <c>dbo.usp_PaymentSettlementCandidates</c> (index seek on CreatedAt); SQLite: LINQ.</summary>
    public Task<IReadOnlyList<LedgerSideRecord>> LoadAsync(DateOnly businessDate, CancellationToken cancellationToken = default) =>
        LedgerSql.SettlementCandidatesAsync(_db, businessDate, cancellationToken);
}

/// <summary>
/// Fetches every provider's settlement file, matches it against our records, stores every item and
/// a run summary, and emits <c>clearpay.reconciliation.completed</c> through the outbox.
/// </summary>
public sealed class SqlReconciliationService : IReconciliationService
{
    private readonly ClearPayDbContext _db;
    private readonly ISettlementFileSource _files;
    private readonly ILedgerSettlementSource _ledger;
    private readonly IClock _clock;
    private readonly ILogger<SqlReconciliationService> _logger;

    public SqlReconciliationService(
        ClearPayDbContext db,
        ISettlementFileSource files,
        ILedgerSettlementSource ledger,
        IClock clock,
        ILogger<SqlReconciliationService> logger)
    {
        _db = db;
        _files = files;
        _ledger = ledger;
        _clock = clock;
        _logger = logger;
    }

    public async Task<ReconciliationRunSummary> RunAsync(DateOnly businessDate, bool force, CancellationToken cancellationToken = default)
    {
        if (!force)
        {
            var done = await _db.ReconciliationRuns.AsNoTracking()
                .Where(r => r.BusinessDate == businessDate && r.Status == "Completed")
                .FirstOrDefaultAsync(cancellationToken)
                .ConfigureAwait(false);
            if (done is not null)
                return Summary(done, skipped: true);
        }

        var run = new ReconciliationRun { Id = Guid.NewGuid(), BusinessDate = businessDate, StartedAt = _clock.UtcNow };
        _db.ReconciliationRuns.Add(run);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var ledger = await _ledger.LoadAsync(businessDate, cancellationToken).ConfigureAwait(false);
            var settlement = new List<SettlementLine>();
            var problems = new List<string>();
            foreach (var provider in _files.Providers)
            {
                var content = await _files.FetchAsync(provider, businessDate, cancellationToken).ConfigureAwait(false);
                if (content is null)
                {
                    problems.Add($"{provider}: no settlement file");
                    continue;
                }

                var (lines, errors) = SettlementCsv.Parse(content);
                settlement.AddRange(lines);
                problems.AddRange(errors.Select(e => $"{provider}: {e}"));
            }

            var items = ReconciliationEngine.Match(ledger, settlement);
            foreach (var item in items)
            {
                _db.ReconciliationItems.Add(new ReconciliationItem
                {
                    Id = Guid.NewGuid(),
                    RunId = run.Id,
                    Kind = item.Kind.ToString(),
                    Provider = item.Provider,
                    Type = item.Type,
                    Reference = item.Reference,
                    OperationId = item.OperationId,
                    LedgerAmount = item.LedgerAmount,
                    SettlementAmount = item.SettlementAmount
                });
                if (item.Kind != ReconciliationItemKind.Matched)
                {
                    _logger.LogWarning(
                        "Reconciliation {BusinessDate} {Provider}: {Kind} {Type} ref {Reference} ledger {LedgerAmount} settlement {SettlementAmount} (operation {OperationId})",
                        businessDate, item.Provider, item.Kind, item.Type, item.Reference, item.LedgerAmount, item.SettlementAmount, item.OperationId);
                }
            }

            run.MatchedCount = items.Count(i => i.Kind == ReconciliationItemKind.Matched);
            run.MismatchCount = items.Count - run.MatchedCount;
            run.LedgerTotal = ledger.Sum(l => Signed(l.Type, l.Amount));
            run.SettlementTotal = settlement.Sum(l => Signed(l.Type, l.Amount));
            run.Error = problems.Count == 0 ? null : string.Join("; ", problems);
            run.Status = "Completed";
            run.CompletedAt = _clock.UtcNow;
            _db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(),
                Type = "clearpay.reconciliation.completed",
                Payload = JsonSerializer.Serialize(new
                {
                    runId = run.Id,
                    businessDate = businessDate.ToString("yyyy-MM-dd"),
                    matched = run.MatchedCount,
                    mismatches = run.MismatchCount,
                    ledgerTotal = run.LedgerTotal,
                    settlementTotal = run.SettlementTotal
                }),
                CorrelationId = run.Id,
                OccurredAt = run.CompletedAt.Value,
                Status = OutboxStatus.Pending
            });
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation(
                "Reconciliation {BusinessDate} completed: {Matched} matched, {Mismatches} mismatches (run {RunId})",
                businessDate, run.MatchedCount, run.MismatchCount, run.Id);
        }
        catch (Exception ex)
        {
            _db.ChangeTracker.Clear();
            _logger.LogError(ex, "Reconciliation {BusinessDate} failed (run {RunId})", businessDate, run.Id);
            var error = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
            await _db.ReconciliationRuns.Where(r => r.Id == run.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(r => r.Status, "Failed")
                    .SetProperty(r => r.Error, error)
                    .SetProperty(r => r.CompletedAt, _clock.UtcNow), CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        return Summary(run, skipped: false);
    }

    public async Task<IReadOnlyList<ReconciliationRunSummary>> ListAsync(int take, CancellationToken cancellationToken = default)
    {
        var runs = await _db.ReconciliationRuns.AsNoTracking().ToListAsync(cancellationToken).ConfigureAwait(false);
        return runs.OrderByDescending(r => r.StartedAt).Take(Math.Clamp(take, 1, 100)).Select(r => Summary(r, false)).ToList();
    }

    public async Task<(ReconciliationRunSummary Run, IReadOnlyList<ReconciliationItemView> Items)?> GetAsync(
        Guid runId, CancellationToken cancellationToken = default)
    {
        var run = await _db.ReconciliationRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken)
            .ConfigureAwait(false);
        if (run is null)
            return null;
        var items = await _db.ReconciliationItems.AsNoTracking().Where(i => i.RunId == runId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return (Summary(run, false), items
            .OrderBy(i => i.Kind == nameof(ReconciliationItemKind.Matched)).ThenBy(i => i.Provider)
            .Select(i => new ReconciliationItemView(i.Kind, i.Provider, i.Type, i.Reference, i.OperationId,
                i.LedgerAmount, i.SettlementAmount, i.Detail))
            .ToList());
    }

    /// <summary>Captures are money in, refunds money out (net settlement per day).</summary>
    private static decimal Signed(string type, decimal amount) =>
        type == nameof(PaymentOperationType.Refund) ? -amount : amount;

    private static ReconciliationRunSummary Summary(ReconciliationRun r, bool skipped) => new(
        r.Id, r.BusinessDate, r.Status, r.MatchedCount, r.MismatchCount, r.LedgerTotal, r.SettlementTotal,
        r.StartedAt, r.CompletedAt, skipped);
}
