using ClearPay.Domain.Payments;

namespace ClearPay.Application.Payments;

public sealed record CreateCardPaymentCommand(
    string UserId,
    Guid ThreeDsSessionId,
    string? Provider,
    bool Capture,
    string IdempotencyKey);

public sealed record PaymentActionCommand(
    string UserId,
    Guid PaymentId,
    decimal? Amount,
    string IdempotencyKey);

public enum PaymentResultKind
{
    Ok = 0,
    Replay = 1,
    MissingKey = 2,
    NotFound = 3,
    InvalidAmount = 4,
    ThreeDsNotAuthenticated = 5,
    ThreeDsAlreadyUsed = 6,
    UnknownProvider = 7,
    InvalidState = 8,
    ExceedsRefundable = 9,
    InsufficientFunds = 10,
    Declined = 11,
    TimedOut = 12,
    KeyPayloadMismatch = 13
}

public sealed record CardPaymentView(
    Guid PaymentId,
    CardPaymentStatus Status,
    string Provider,
    string? ProviderReference,
    decimal Amount,
    decimal CapturedAmount,
    decimal RefundedAmount,
    string CardLast4,
    Guid CorrelationId,
    IReadOnlyList<PaymentOperationView> Operations);

public sealed record PaymentOperationView(
    Guid OperationId,
    PaymentOperationType Type,
    PaymentOperationStatus Status,
    decimal Amount,
    string? ProviderReference,
    DateTimeOffset CreatedAt);

public sealed record PaymentResult(
    PaymentResultKind Kind,
    CardPaymentView? Payment,
    Guid? OperationId = null,
    string? DeclineCode = null)
{
    public bool IsSuccess => Kind == PaymentResultKind.Ok;

    public static PaymentResult Fail(PaymentResultKind kind, CardPaymentView? payment = null, string? declineCode = null) =>
        new(kind, payment, null, declineCode);
}
