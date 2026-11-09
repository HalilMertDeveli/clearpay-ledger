using ClearPay.Application.Reconciliation;

namespace ClearPay.Application.Ports;

/// <summary>Where the PSP's settlement file for a provider and business date comes from (SFTP, bucket, ... - here: the simulator).</summary>
public interface ISettlementFileSource
{
    IReadOnlyCollection<string> Providers { get; }

    /// <summary>File content, or null when the PSP has not delivered a file for that date.</summary>
    Task<string?> FetchAsync(string provider, DateOnly businessDate, CancellationToken cancellationToken = default);
}

/// <summary>Our own record of captures and refunds per provider and business date.</summary>
public interface ILedgerSettlementSource
{
    Task<IReadOnlyList<LedgerSideRecord>> LoadAsync(DateOnly businessDate, CancellationToken cancellationToken = default);
}

public sealed record ReconciliationRunSummary(
    Guid RunId,
    DateOnly BusinessDate,
    string Status,
    int Matched,
    int Mismatches,
    decimal LedgerTotal,
    decimal SettlementTotal,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    bool Skipped);

public sealed record ReconciliationItemView(
    string Kind,
    string Provider,
    string Type,
    string? Reference,
    Guid? OperationId,
    decimal? LedgerAmount,
    decimal? SettlementAmount,
    string? Detail);

public interface IReconciliationService
{
    /// <summary>Runs for a business date. Without <paramref name="force"/> a date that already has a completed run is skipped.</summary>
    Task<ReconciliationRunSummary> RunAsync(DateOnly businessDate, bool force, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ReconciliationRunSummary>> ListAsync(int take, CancellationToken cancellationToken = default);

    Task<(ReconciliationRunSummary Run, IReadOnlyList<ReconciliationItemView> Items)?> GetAsync(
        Guid runId, CancellationToken cancellationToken = default);
}

/// <summary>The PSP side of the end-of-day process (simulated): drops one settlement file per provider.</summary>
public interface IPspSettlementFileWriter
{
    /// <summary>Returns the paths written.</summary>
    Task<IReadOnlyList<string>> WriteAsync(DateOnly businessDate, CancellationToken cancellationToken = default);
}
