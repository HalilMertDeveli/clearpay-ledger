using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace ClearPay.Application.Payments;

/// <summary>
/// HMAC-SHA256 signatures between the gateway and the fake bank ACS.
/// Two message kinds use different prefixes so a challenge request (CReq) can never be
/// replayed as a challenge result (CRes). Comparison is constant-time.
/// </summary>
public static class ThreeDsSigner
{
    public const string ChallengeRequestPrefix = "clearpay.creq.v1";
    public const string ChallengeResultPrefix = "clearpay.cres.v1";

    public static string CanonicalChallengeRequest(Guid sessionId, decimal amount, string last4, long expiresAtUnix) =>
        string.Join('\n',
            ChallengeRequestPrefix,
            sessionId.ToString("D"),
            amount.ToString("F2", CultureInfo.InvariantCulture),
            last4,
            expiresAtUnix.ToString(CultureInfo.InvariantCulture));

    public static string CanonicalChallengeResult(AcsChallengeResult result) =>
        string.Join('\n',
            ChallengeResultPrefix,
            result.SessionId.ToString("D"),
            result.TransStatus,
            result.Eci ?? string.Empty,
            result.AuthenticationValue ?? string.Empty,
            result.IssuedAtUnix.ToString(CultureInfo.InvariantCulture),
            result.Nonce);

    public static string Sign(string canonical, string secret)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var mac = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical));
        return Base64Url(mac);
    }

    public static bool Verify(string canonical, string? signature, string secret)
    {
        if (string.IsNullOrWhiteSpace(signature))
            return false;

        var expected = Encoding.ASCII.GetBytes(Sign(canonical, secret));
        var actual = Encoding.ASCII.GetBytes(signature.Trim());
        return CryptographicOperations.FixedTimeEquals(expected, actual);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
