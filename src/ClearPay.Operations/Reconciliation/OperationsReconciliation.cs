using System.Data;
using System.Text.Json;
using ClearPay.Application.Ports;
using ClearPay.Application.Reconciliation;
using ClearPay.Domain.Reconciliation;
using ClearPay.Messaging;
using ClearPay.Operations.Data;
using Hangfire;
using Microsoft.EntityFrameworkCore;

namespace ClearPay.Operations.Reconciliation;

/// <summary>Reads <c>{provider}_{yyyyMMdd}.csv</c> from the PSP drop folder. A missing file is reported, not invented.</summary>
public sealed class FileSystemSettlementFileSource : ISettlementFileSource
{
    private readonly string _directory;

    public FileSystemSettlementFileSource(IConfiguration configuration)
    {
        var configured = configuration["Reconciliation:SettlementDirectory"];
        _directory = string.IsNullOrWhiteSpace(configured) ? Path.Combine(Path.GetTempPath(), "clearpay-settlements") : configured;
        Providers = (configuration["Reconciliation:Providers"] ?? "simbank-rest,simbank-soap,simpay")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    public IReadOnlyCollection<string> Providers { get; }

    public async Task<string?> FetchAsync(string provider, DateOnly businessDate, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(_directory, $"{provider}_{businessDate:yyyyMMdd}.csv");
        return File.Exists(path) ? await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false) : null;
    }
}

/// <summary>Our side, from the event-fed projection (SQL Server: <c>ops.usp_SettlementCandidates</c>).</summary>
public sealed class ProjectionSettlementSource : ILedgerSettlementSource
{
    private readonly OperationsDbContext _db;

    public ProjectionSettlementSource(OperationsDbContext db)
    {
        _db = db;
    }

    public async Task<IReadOnlyList<LedgerSideRecord>> LoadAsync(DateOnly businessDate, CancellationToken cancellationToken = default)
    {
        var from = new DateTimeOffset(businessDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);
        if (_db.Database.IsSqlServer())
        {
            var connection = _db.Database.GetDbConnection();
            if (connection.State != ConnectionState.Open)
                await _db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "ops.usp_SettlementCandidates";
            command.CommandType = CommandType.StoredProcedure;
            foreach (var (name, value) in new[] { ("@From", from), ("@To", to) })
            {
                var p = command.CreateParameter();
                p.ParameterName = name;
                p.DbType = DbType.DateTimeOffset;
                p.Value = value;
                command.Parameters.Add(p);
            }

            var rows = new List<LedgerSideRecord>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(new LedgerSideRecord(reader.GetGuid(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.IsDBNull(4) ? null : reader.GetString(4), reader.GetGuid(5), reader.GetDecimal(6)));
            }

            return rows;
        }

        var all = await _db.PaymentOperations.AsNoTracking()
            .Where(p => (p.Type == "Capture" || p.Type == "Refund") && p.Status != "Failed")
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return all.Where(p => p.CreatedAt >= from && p.CreatedAt < to)
            .Select(p => new LedgerSideRecord(p.OperationId, p.Provider, p.Type, p.Status, p.ProviderReference, p.MerchantReference, p.Amount))
            .ToList();
    }
}

/// <summary>
/// Fetch settlement files, match them against the projection, store every item and a summary, then publish
/// <c>clearpay.reconciliation.completed</c> (best effort after commit: the stored run is the source of truth).
/// </summary>
public sealed class OperationsReconciliationService : IReconciliationService
{
    private readonly OperationsDbContext _db;
    private readonly ISettlementFileSource _files;
    private readonly ILedgerSettlementSource _ledger;
    private readonly TimeProvider _time;
    private readonly RabbitOptions _rabbit;
    private readonly IServiceProvider _services;
    private readonly ILogger<OperationsReconciliationService> _logger;

    public OperationsReconciliationService(
        OperationsDbContext db,
        ISettlementFileSource files,
        ILedgerSettlementSource ledger,
        TimeProvider time,
        RabbitOptions rabbit,
        IServiceProvider services,
        ILogger<OperationsReconciliationService> logger)
    {
        _db = db;
        _files = files;
        _ledger = ledger;
        _time = time;
        _rabbit = rabbit;
        _services = services;
        _logger = logger;
    }

    public async Task<ReconciliationRunSummary> RunAsync(DateOnly businessDate, bool force, CancellationToken cancellationToken = default)
    {
        if (!force)
        {
            var done = await _db.ReconciliationRuns.AsNoTracking()
                .FirstOrDefaultAsync(r => r.BusinessDate == businessDate && r.Status == "Completed", cancellationToken)
                .ConfigureAwait(false);
            if (done is not null)
                return Summary(done, skipped: true);
        }

        var run = new ReconciliationRun { Id = Guid.NewGuid(), BusinessDate = businessDate, StartedAt = _time.GetUtcNow() };
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
            run.LedgerTotal = ledger.Sum(l => l.Type == "Refund" ? -l.Amount : l.Amount);
            run.SettlementTotal = settlement.Sum(l => l.Type == "Refund" ? -l.Amount : l.Amount);
            run.Error = problems.Count == 0 ? null : string.Join("; ", problems);
            run.Status = "Completed";
            run.CompletedAt = _time.GetUtcNow();
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
            var failedAt = _time.GetUtcNow();
            await _db.ReconciliationRuns.Where(r => r.Id == run.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(r => r.Status, "Failed").SetProperty(r => r.Error, error)
                    .SetProperty(r => r.CompletedAt, failedAt), CancellationToken.None)
                .ConfigureAwait(false);
            throw;
        }

        PublishCompleted(run);
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
        var run = await _db.ReconciliationRuns.AsNoTracking().FirstOrDefaultAsync(r => r.Id == runId, cancellationToken).ConfigureAwait(false);
        if (run is null)
            return null;
        var items = await _db.ReconciliationItems.AsNoTracking().Where(i => i.RunId == runId).ToListAsync(cancellationToken).ConfigureAwait(false);
        return (Summary(run, false), items
            .OrderBy(i => i.Kind == nameof(ReconciliationItemKind.Matched)).ThenBy(i => i.Provider)
            .Select(i => new ReconciliationItemView(i.Kind, i.Provider, i.Type, i.Reference, i.OperationId, i.LedgerAmount, i.SettlementAmount, i.Detail))
            .ToList());
    }

    private void PublishCompleted(ReconciliationRun run)
    {
        if (!_rabbit.Enabled)
            return;
        try
        {
            var payload = JsonSerializer.Serialize(new
            {
                runId = run.Id,
                businessDate = run.BusinessDate.ToString("yyyy-MM-dd"),
                matched = run.MatchedCount,
                mismatches = run.MismatchCount,
                ledgerTotal = run.LedgerTotal,
                settlementTotal = run.SettlementTotal
            });
            _services.GetRequiredService<RabbitEventPublisher>().Publish(run.Id, "clearpay.reconciliation.completed", payload, run.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not publish reconciliation.completed for run {RunId}; the run itself is stored", run.Id);
        }
    }

    private static ReconciliationRunSummary Summary(ReconciliationRun r, bool skipped) => new(
        r.Id, r.BusinessDate, r.Status, r.MatchedCount, r.MismatchCount, r.LedgerTotal, r.SettlementTotal, r.StartedAt, r.CompletedAt, skipped);
}

/// <summary>Nightly (02:30 UTC, after the PSPs drop their files at 01:00): reconcile yesterday.</summary>
public sealed class ReconciliationJob
{
    private readonly IReconciliationService _service;
    private readonly TimeProvider _time;

    public ReconciliationJob(IReconciliationService service, TimeProvider time)
    {
        _service = service;
        _time = time;
    }

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(600)]
    public Task Run() =>
        _service.RunAsync(DateOnly.FromDateTime(_time.GetUtcNow().UtcDateTime).AddDays(-1), force: false, CancellationToken.None);
}
