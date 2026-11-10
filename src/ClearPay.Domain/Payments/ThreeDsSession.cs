namespace ClearPay.Domain.Payments;

/// <summary>
/// Lifecycle of one simulated 3-D Secure authentication.
/// Initiated -> Challenge -> Authenticated | Failed | TimedOut.
/// Initiated may also go straight to Failed or TimedOut. The last three are terminal.
/// </summary>
public enum ThreeDsStatus
{
    Initiated = 0,
    Challenge = 1,
    Authenticated = 2,
    Failed = 3,
    TimedOut = 4
}

/// <summary>
/// One 3-D Secure authentication started by the merchant (the wallet app) for a card payment.
/// Demo only: the "bank" is the in-process fake ACS page; only the last four digits are stored.
/// State changes go through <see cref="TryMoveTo"/> so illegal transitions are impossible.
/// </summary>
public sealed class ThreeDsSession
{
    public Guid Id { get; set; }

    public string UserId { get; set; } = string.Empty;

    public decimal Amount { get; set; }

    public string Currency { get; set; } = "TRY";

    public string CardLast4 { get; set; } = string.Empty;

    public ThreeDsStatus Status { get; set; } = ThreeDsStatus.Initiated;

    /// <summary>Electronic Commerce Indicator returned by the ACS (05 = fully authenticated in this demo).</summary>
    public string? Eci { get; set; }

    /// <summary>Opaque authentication value (CAVV-like) from the ACS. Random demo value.</summary>
    public string? AuthenticationValue { get; set; }

    public string? FailureReason { get; set; }

    /// <summary>Nonce of the ACS result that was applied. A second delivery of the same result is a no-op.</summary>
    public string? AcsNonce { get; set; }

    public Guid CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Set once a payment has used this authentication, so it cannot be reused.</summary>
    public Guid? ConsumedByPaymentId { get; set; }

    public bool IsTerminal => IsTerminalStatus(Status);

    public static bool IsTerminalStatus(ThreeDsStatus status) =>
        status is ThreeDsStatus.Authenticated or ThreeDsStatus.Failed or ThreeDsStatus.TimedOut;

    public static bool CanMove(ThreeDsStatus from, ThreeDsStatus to) => (from, to) switch
    {
        (ThreeDsStatus.Initiated, ThreeDsStatus.Challenge) => true,
        (ThreeDsStatus.Initiated, ThreeDsStatus.Failed) => true,
        (ThreeDsStatus.Initiated, ThreeDsStatus.TimedOut) => true,
        (ThreeDsStatus.Challenge, ThreeDsStatus.Authenticated) => true,
        (ThreeDsStatus.Challenge, ThreeDsStatus.Failed) => true,
        (ThreeDsStatus.Challenge, ThreeDsStatus.TimedOut) => true,
        _ => false
    };

    public bool IsExpired(DateTimeOffset now) => !IsTerminal && now >= ExpiresAt;

    /// <summary>Applies a transition if it is legal. Returns false and changes nothing otherwise.</summary>
    public bool TryMoveTo(ThreeDsStatus next, DateTimeOffset now, string? reason = null)
    {
        if (!CanMove(Status, next))
            return false;

        Status = next;
        UpdatedAt = now;
        if (next is ThreeDsStatus.Failed or ThreeDsStatus.TimedOut)
            FailureReason = reason ?? next.ToString();
        return true;
    }
}
