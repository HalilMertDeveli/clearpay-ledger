using ClearPay.Application.Payments;
using ClearPay.Application.Ports;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace ClearPay.Web.Pages;

/// <summary>
/// "SimBank" issuer challenge page. Plays the bank: it trusts only the signed CReq, asks for the
/// demo OTP and hands the browser a signed CRes to post to the gateway callback.
/// </summary>
public sealed class FakeAcsModel : PageModel
{
    private readonly IFakeAcs _acs;

    public FakeAcsModel(IFakeAcs acs)
    {
        _acs = acs;
    }

    [BindProperty(SupportsGet = true)]
    public string? Creq { get; set; }

    [BindProperty]
    public string? Otp { get; set; }

    public AcsChallenge? ChallengeData { get; private set; }

    public AcsChallengeResult? Result { get; private set; }

    public IActionResult OnGet()
    {
        ChallengeData = _acs.ReadChallenge(Creq);
        return ChallengeData is null ? BadRequest("Invalid or tampered challenge request.") : Page();
    }

    public IActionResult OnPost(string? action)
    {
        ChallengeData = _acs.ReadChallenge(Creq);
        if (ChallengeData is null)
            return BadRequest("Invalid or tampered challenge request.");

        Result = _acs.Complete(ChallengeData, Otp, cancelled: string.Equals(action, "cancel", StringComparison.Ordinal));
        return Page();
    }
}
