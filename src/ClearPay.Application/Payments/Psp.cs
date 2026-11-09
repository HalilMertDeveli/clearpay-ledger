namespace ClearPay.Application.Payments;

/// <summary>
/// One payment service provider / acquiring bank. Every adapter speaks its own wire format
/// (JSON, SOAP XML, minor-unit integers) and maps it to these neutral records, so the payment
/// service never knows which provider it is talking to.
/// Every call carries an idempotency key; a provider must answer a repeated key with the
/// original result instead of executing the operation again.
/// </summary>
public interface IPaymentServiceProvider
{
    /// <summary>Stable adapter name stored on the payment, for example <c>simbank-rest</c>.</summary>
    string Name { get; }

    Task<PspResult> AuthorizeAsync(PspAuthorizeRequest request, CancellationToken cancellationToken = default);

    Task<PspResult> CaptureAsync(PspFollowUpRequest request, CancellationToken cancellationToken = default);

    Task<PspResult> RefundAsync(PspFollowUpRequest request, CancellationToken cancellationToken = default);

    Task<PspResult> VoidAsync(PspFollowUpRequest request, CancellationToken cancellationToken = default);
}

/// <summary>Picks the adapter for a payment (explicit name, otherwise the configured default).</summary>
public interface IPspRouter
{
    IReadOnlyCollection<string> Names { get; }

    IPaymentServiceProvider? Resolve(string? name);
}

/// <summary>Authorization carries the 3-D Secure proof (ECI + authentication value) for liability shift.</summary>
public sealed record PspAuthorizeRequest(
    string IdempotencyKey,
    decimal Amount,
    string Currency,
    string CardLast4,
    string Eci,
    string AuthenticationValue,
    Guid CorrelationId);

/// <summary>Capture / refund / void of an earlier authorization identified by its provider reference.</summary>
public sealed record PspFollowUpRequest(
    string IdempotencyKey,
    string AuthorizationReference,
    decimal Amount,
    Guid CorrelationId);

public enum PspOutcome
{
    Approved = 0,
    Declined = 1,
    TimedOut = 2
}

public sealed record PspResult(PspOutcome Outcome, string? Reference, string? DeclineCode)
{
    public static PspResult Approved(string reference) => new(PspOutcome.Approved, reference, null);

    public static PspResult Declined(string code) => new(PspOutcome.Declined, null, code);

    public static PspResult TimedOut() => new(PspOutcome.TimedOut, null, null);
}
