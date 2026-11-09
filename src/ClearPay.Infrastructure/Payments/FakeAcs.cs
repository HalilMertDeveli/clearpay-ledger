using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClearPay.Application.Payments;
using ClearPay.Application.Ports;

namespace ClearPay.Infrastructure.Payments;

/// <summary>
/// Simulated issuer ACS ("SimBank"). Reads the gateway-signed CReq, checks the demo OTP and
/// returns a signed CRes. It has no database access: everything it knows comes from the CReq.
/// </summary>
public sealed class FakeAcs : IFakeAcs
{
    private readonly ThreeDsOptions _options;
    private readonly IClock _clock;

    public FakeAcs(ThreeDsOptions options, IClock clock)
    {
        _options = options;
        _clock = clock;
    }

    public AcsChallenge? ReadChallenge(string? creq) => ChallengeToken.Read(creq, _options.AcsSharedSecret);

    public AcsChallengeResult Complete(AcsChallenge challenge, string? otp, bool cancelled)
    {
        ArgumentNullException.ThrowIfNull(challenge);
        var success = !cancelled
            && string.Equals(otp?.Trim(), ThreeDsTestCards.DemoOtp, StringComparison.Ordinal);
        var unsigned = new AcsChallengeResult(
            challenge.SessionId,
            TransStatus: success ? "Y" : "N",
            Eci: success ? "05" : "07",
            AuthenticationValue: success ? Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)) : null,
            IssuedAtUnix: _clock.UtcNow.ToUnixTimeSeconds(),
            Nonce: Convert.ToHexString(RandomNumberGenerator.GetBytes(16)),
            Signature: null);
        var signature = ThreeDsSigner.Sign(ThreeDsSigner.CanonicalChallengeResult(unsigned), _options.AcsSharedSecret);
        return unsigned with { Signature = signature };
    }
}

/// <summary>CReq wire format: base64url(JSON) + "." + HMAC over the canonical fields.</summary>
public static class ChallengeToken
{
    private sealed record Payload(Guid Sid, string Amt, string L4, long Exp);

    public static string Create(Guid sessionId, decimal amount, string last4, DateTimeOffset expiresAt, string secret)
    {
        var exp = expiresAt.ToUnixTimeSeconds();
        var json = JsonSerializer.SerializeToUtf8Bytes(
            new Payload(sessionId, amount.ToString("F2", CultureInfo.InvariantCulture), last4, exp));
        var signature = ThreeDsSigner.Sign(
            ThreeDsSigner.CanonicalChallengeRequest(sessionId, amount, last4, exp), secret);
        return Encode(json) + "." + signature;
    }

    public static AcsChallenge? Read(string? token, string secret)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > 2048)
            return null;
        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == token.Length - 1)
            return null;

        try
        {
            var payload = JsonSerializer.Deserialize<Payload>(Decode(token[..dot]));
            if (payload is null
                || !decimal.TryParse(payload.Amt, NumberStyles.Number, CultureInfo.InvariantCulture, out var amount))
                return null;

            var canonical = ThreeDsSigner.CanonicalChallengeRequest(payload.Sid, amount, payload.L4, payload.Exp);
            if (!ThreeDsSigner.Verify(canonical, token[(dot + 1)..], secret))
                return null;

            return new AcsChallenge(payload.Sid, amount, payload.L4, DateTimeOffset.FromUnixTimeSeconds(payload.Exp));
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    private static string Encode(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static byte[] Decode(string text)
    {
        var s = text.Replace('-', '+').Replace('_', '/');
        s = s.PadRight(s.Length + ((4 - (s.Length % 4)) % 4), '=');
        return Convert.FromBase64String(s);
    }
}
