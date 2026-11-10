using ClearPay.Domain.Payments;

namespace ClearPay.Application.Payments;

/// <summary>Merchant asks the gateway to authenticate a card payment. The PAN is used for Luhn and last four only, never stored.</summary>
public sealed record ThreeDsInitiateCommand(
    string UserId,
    decimal Amount,
    string CardNumber,
    string IdempotencyKey);

public enum ThreeDsInitiateKind
{
    Created = 0,
    Replay = 1,
    MissingKey = 2,
    InvalidAmount = 3,
    InvalidCard = 4
}

public sealed record ThreeDsInitiateOutcome(
    ThreeDsInitiateKind Kind,
    Guid SessionId,
    ThreeDsStatus Status,
    string? AcsUrl)
{
    public static ThreeDsInitiateOutcome Fail(ThreeDsInitiateKind kind) =>
        new(kind, Guid.Empty, ThreeDsStatus.Failed, null);
}

/// <summary>Values the fake ACS page needs to render the challenge; decoded from the signed CReq.</summary>
public sealed record AcsChallenge(Guid SessionId, decimal Amount, string CardLast4, DateTimeOffset ExpiresAt);

/// <summary>
/// Challenge result (CRes) the ACS posts back through the browser.
/// TransStatus: Y = authenticated, N = not authenticated.
/// </summary>
public sealed record AcsChallengeResult(
    Guid SessionId,
    string TransStatus,
    string? Eci,
    string? AuthenticationValue,
    long IssuedAtUnix,
    string Nonce,
    string? Signature);

public enum ThreeDsCallbackKind
{
    Applied = 0,
    AlreadyApplied = 1,
    InvalidSignature = 2,
    Stale = 3,
    NotFound = 4,
    Expired = 5,
    Conflict = 6
}

public sealed record ThreeDsCallbackOutcome(ThreeDsCallbackKind Kind, Guid SessionId, ThreeDsStatus? Status);

public sealed record ThreeDsSessionView(
    Guid SessionId,
    ThreeDsStatus Status,
    decimal Amount,
    string Currency,
    string CardLast4,
    string? Eci,
    string? FailureReason,
    Guid CorrelationId,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    DateTimeOffset UpdatedAt);

/// <summary>Demo test cards. Any other Luhn-valid number is challenged with OTP <see cref="DemoOtp"/>.</summary>
public static class ThreeDsTestCards
{
    public const string DemoOtp = "123456";

    /// <summary>Last four digits the fake issuer refuses to authenticate (frictionless N).</summary>
    public const string DeclinedLast4 = "0002";

    public static bool IsLuhnValid(string? digits)
    {
        if (string.IsNullOrWhiteSpace(digits))
            return false;
        var clean = digits.Replace(" ", string.Empty, StringComparison.Ordinal);
        if (clean.Length is < 12 or > 19 || !clean.All(char.IsAsciiDigit))
            return false;

        var sum = 0;
        var doubleIt = false;
        for (var i = clean.Length - 1; i >= 0; i--)
        {
            var d = clean[i] - '0';
            if (doubleIt)
            {
                d *= 2;
                if (d > 9)
                    d -= 9;
            }

            sum += d;
            doubleIt = !doubleIt;
        }

        return sum % 10 == 0;
    }

    public static string Last4(string cardNumber)
    {
        var clean = cardNumber.Replace(" ", string.Empty, StringComparison.Ordinal);
        return clean[^4..];
    }
}
