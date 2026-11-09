using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace ClearPay.Infrastructure.Payments;

/// <summary>
/// Shared HMAC key between the gateway and the fake ACS, plus the challenge deadline.
/// Same rule as the JWT key: Development may fall back to a public placeholder, every other
/// environment must configure <c>ThreeDs:AcsSharedSecret</c> (env var <c>ThreeDs__AcsSharedSecret</c>).
/// </summary>
public sealed class ThreeDsOptions
{
    /// <summary>Public placeholder, not a secret. Never accepted outside Development.</summary>
    public const string DevelopmentSecret = "dev-only-placeholder-3ds-acs-key-NOT-A-SECRET";

    public ThreeDsOptions(string acsSharedSecret, TimeSpan challengeTimeout, TimeSpan callbackMaxAge)
    {
        AcsSharedSecret = acsSharedSecret;
        ChallengeTimeout = challengeTimeout;
        CallbackMaxAge = callbackMaxAge;
    }

    public string AcsSharedSecret { get; }

    public TimeSpan ChallengeTimeout { get; }

    public TimeSpan CallbackMaxAge { get; }

    public static ThreeDsOptions From(IConfiguration configuration, IHostEnvironment environment)
    {
        var secret = configuration["ThreeDs:AcsSharedSecret"];
        if (string.IsNullOrWhiteSpace(secret))
        {
            if (!environment.IsDevelopment())
            {
                throw new InvalidOperationException(
                    "ThreeDs:AcsSharedSecret is required outside Development (environment variable ThreeDs__AcsSharedSecret). Do not commit a real key.");
            }

            secret = DevelopmentSecret;
        }

        if (Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("ThreeDs:AcsSharedSecret must be at least 32 bytes.");

        var timeout = TimeSpan.FromSeconds(configuration.GetValue("ThreeDs:ChallengeTimeoutSeconds", 600));
        var maxAge = TimeSpan.FromSeconds(configuration.GetValue("ThreeDs:CallbackMaxAgeSeconds", 300));
        return new ThreeDsOptions(secret, timeout, maxAge);
    }
}
