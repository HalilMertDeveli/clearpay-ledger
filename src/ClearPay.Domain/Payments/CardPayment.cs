namespace ClearPay.Domain.Payments;

public enum CardPaymentStatus
{
    /// <summary>3-D Secure claimed, authorization sent to the PSP, no final answer yet.</summary>
    Pending = 0,
    Authorized = 1,
    Captured = 2,
    PartiallyRefunded = 3,
    Refunded = 4,
    Voided = 5,
    Declined = 6
}

public enum PaymentOperationType
{
    Authorize = 0,
    Capture = 1,
    Refund = 2,
    Void = 3
}

/// <summary>Pending = sent to the PSP without a final answer (timeout); reconciliation settles it.</summary>
public enum PaymentOperationStatus
{
    Pending = 0,
    Succeeded = 1,
    Failed = 2
}

/// <summary>
/// A card payment that funds the customer's wallet after a successful 3-D Secure authentication.
/// Money rules live here: captured once, refunds never exceed the captured amount, void only
/// before capture. The ledger rows themselves are built by <c>LedgerPair</c> in the executor.
/// </summary>
public sealed class CardPayment
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public Guid ThreeDsSessionId { get; set; }

    /// <summary>Adapter name, for example <c>simbank-rest</c>.</summary>
    public string Provider { get; set; } = string.Empty;

    /// <summary>PSP's id for the authorization; refunds and voids refer to it.</summary>
    public string? ProviderReference { get; set; }

    public decimal Amount { get; set; }

    public decimal CapturedAmount { get; set; }

    /// <summary>Includes refunds still waiting for a PSP answer (reserved, already debited from the wallet).</summary>
    public decimal RefundedAmount { get; set; }

    public string CardLast4 { get; set; } = string.Empty;

    public CardPaymentStatus Status { get; set; }

    public string? DeclineCode { get; set; }

    public Guid CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    public decimal RefundableAmount => CapturedAmount - RefundedAmount;

    public bool CanCapture => Status == CardPaymentStatus.Authorized;

    public bool CanVoid => Status == CardPaymentStatus.Authorized;

    public bool CanRefund(decimal amount) =>
        Status is CardPaymentStatus.Captured or CardPaymentStatus.PartiallyRefunded
        && amount > 0m
        && amount <= RefundableAmount;

    public void MarkAuthorized(string providerReference, DateTimeOffset now)
    {
        if (Status != CardPaymentStatus.Pending)
            throw new InvalidOperationException($"Cannot authorize a {Status} payment.");
        ProviderReference = providerReference;
        Status = CardPaymentStatus.Authorized;
        UpdatedAt = now;
    }

    public void MarkDeclined(string? code, DateTimeOffset now)
    {
        if (Status != CardPaymentStatus.Pending)
            throw new InvalidOperationException($"Cannot decline a {Status} payment.");
        DeclineCode = code;
        Status = CardPaymentStatus.Declined;
        UpdatedAt = now;
    }

    public void MarkCaptured(DateTimeOffset now)
    {
        if (!CanCapture)
            throw new InvalidOperationException($"Cannot capture a {Status} payment.");
        CapturedAmount = Amount;
        Status = CardPaymentStatus.Captured;
        UpdatedAt = now;
    }

    public void MarkVoided(DateTimeOffset now)
    {
        if (!CanVoid)
            throw new InvalidOperationException($"Cannot void a {Status} payment.");
        Status = CardPaymentStatus.Voided;
        UpdatedAt = now;
    }

    /// <summary>Reserves a refund. Called inside the same transaction that debits the wallet.</summary>
    public void ReserveRefund(decimal amount, DateTimeOffset now)
    {
        if (!CanRefund(amount))
            throw new InvalidOperationException("Refund exceeds the refundable amount or the payment is not captured.");
        RefundedAmount += amount;
        Status = RefundedAmount == CapturedAmount ? CardPaymentStatus.Refunded : CardPaymentStatus.PartiallyRefunded;
        UpdatedAt = now;
    }

    /// <summary>Gives a reserved refund back when the PSP declines it (the wallet debit is reversed too).</summary>
    public void ReleaseRefund(decimal amount, DateTimeOffset now)
    {
        if (amount <= 0m || amount > RefundedAmount)
            throw new InvalidOperationException("Cannot release more than was reserved.");
        RefundedAmount -= amount;
        Status = RefundedAmount == 0m ? CardPaymentStatus.Captured : CardPaymentStatus.PartiallyRefunded;
        UpdatedAt = now;
    }
}

/// <summary>One call to the PSP. Reconciliation compares these rows with the PSP settlement file.</summary>
public sealed class PaymentOperation
{
    public Guid Id { get; set; }

    public Guid PaymentId { get; set; }

    public PaymentOperationType Type { get; set; }

    public PaymentOperationStatus Status { get; set; }

    public decimal Amount { get; set; }

    public string Provider { get; set; } = string.Empty;

    /// <summary>PSP's reference for this operation (null while Pending after a timeout).</summary>
    public string? ProviderReference { get; set; }

    /// <summary>Key sent to the PSP so a retry of the same operation is not executed twice there.</summary>
    public string ProviderIdempotencyKey { get; set; } = string.Empty;

    public Guid CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}
