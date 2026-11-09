namespace ClearPay.Domain.Payments;

/// <summary>
/// The fake PSPs' own record of what they did ("bank side"). It lives in the <c>sim</c> schema
/// and is written only by the simulators, never by the payment service. Settlement files for
/// reconciliation are produced from these rows, not from the ledger.
/// </summary>
public sealed class SimPspTransaction
{
    public Guid Id { get; set; }

    public string Provider { get; set; } = string.Empty;

    /// <summary>Unique per provider: a repeated key returns this row instead of executing again.</summary>
    public string IdempotencyKey { get; set; } = string.Empty;

    public PaymentOperationType Type { get; set; }

    /// <summary>For capture/refund/void: the authorization this operation belongs to.</summary>
    public string? AuthorizationReference { get; set; }

    public string Reference { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public bool Approved { get; set; }

    public string? DeclineCode { get; set; }

    /// <summary>The PSP executed the operation but the caller saw a timeout (lost response).</summary>
    public bool ResponseLost { get; set; }

    public Guid CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}
