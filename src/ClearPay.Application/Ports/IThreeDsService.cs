using ClearPay.Application.Payments;

namespace ClearPay.Application.Ports;

/// <summary>Gateway side of the simulated 3-D Secure flow: start, read, verify the ACS callback, expire.</summary>
public interface IThreeDsService
{
    Task<ThreeDsInitiateOutcome> InitiateAsync(ThreeDsInitiateCommand command, CancellationToken cancellationToken = default);

    /// <summary>Only the session owner can read it; others get null (404).</summary>
    Task<ThreeDsSessionView?> GetAsync(Guid sessionId, string userId, CancellationToken cancellationToken = default);

    Task<ThreeDsCallbackOutcome> HandleCallbackAsync(AcsChallengeResult result, CancellationToken cancellationToken = default);

    /// <summary>Moves every non-terminal session past its deadline to TimedOut. Returns how many moved.</summary>
    Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default);
}

/// <summary>
/// The fake issuer's Access Control Server. Stateless: it trusts only the signed CReq and
/// answers with a signed CRes. Lives in-process for the demo but shares nothing with the gateway except the key.
/// </summary>
public interface IFakeAcs
{
    AcsChallenge? ReadChallenge(string? creq);

    AcsChallengeResult Complete(AcsChallenge challenge, string? otp, bool cancelled);
}
