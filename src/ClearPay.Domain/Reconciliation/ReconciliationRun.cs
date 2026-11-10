namespace ClearPay.Domain.Reconciliation;

/// <summary>One reconciliation of all providers for one business date (UTC).</summary>
public sealed class ReconciliationRun
{
    public Guid Id { get; set; }

    public DateOnly BusinessDate { get; set; }

    /// <summary>Running, Completed or Failed.</summary>
    public string Status { get; set; } = "Running";

    public int MatchedCount { get; set; }

    public int MismatchCount { get; set; }

    public decimal LedgerTotal { get; set; }

    public decimal SettlementTotal { get; set; }

    public string? Error { get; set; }

    public DateTimeOffset StartedAt { get; set; }

    public DateTimeOffset? CompletedAt { get; set; }
}

/// <summary>One matched or mismatched line; mismatches are what an operator works through the next morning.</summary>
public sealed class ReconciliationItem
{
    public Guid Id { get; set; }

    public Guid RunId { get; set; }

    public string Kind { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string? Reference { get; set; }

    public Guid? OperationId { get; set; }

    public decimal? LedgerAmount { get; set; }

    public decimal? SettlementAmount { get; set; }

    public string? Detail { get; set; }
}
