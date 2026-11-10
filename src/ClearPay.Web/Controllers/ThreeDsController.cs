using System.Security.Claims;
using ClearPay.Application.Payments;
using ClearPay.Application.Ports;
using ClearPay.Web.Api;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ClearPay.Web.Controllers;

/// <summary>
/// Simulated 3-D Secure: merchant (this app) -> gateway (this controller) -> fake bank ACS page
/// (<c>/fake-acs</c>) -> signed callback. Demo only; no card network or issuer is contacted.
/// </summary>
[ApiController]
public sealed class ThreeDsController : ControllerBase
{
    private readonly IThreeDsService _threeDs;

    public ThreeDsController(IThreeDsService threeDs)
    {
        _threeDs = threeDs;
    }

    [HttpPost("/api/3ds/sessions")]
    [JwtApi]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Initiate([FromBody] ThreeDsInitiateApiRequest body, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actor))
            return Unauthorized();

        var outcome = await _threeDs.InitiateAsync(
            new ThreeDsInitiateCommand(
                actor,
                body?.Amount ?? 0m,
                body?.CardNumber ?? string.Empty,
                Request.Headers["Idempotency-Key"].ToString()),
            cancellationToken).ConfigureAwait(false);

        return outcome.Kind switch
        {
            ThreeDsInitiateKind.Created => Created(
                $"/api/3ds/sessions/{outcome.SessionId}",
                new { sessionId = outcome.SessionId, status = outcome.Status.ToString(), acsUrl = outcome.AcsUrl }),
            ThreeDsInitiateKind.Replay => ConflictWith(outcome),
            ThreeDsInitiateKind.MissingKey => Problem(
                title: "Idempotency-Key required",
                detail: "Send the Idempotency-Key header. The same key returns 409 with the existing session.",
                statusCode: StatusCodes.Status400BadRequest),
            ThreeDsInitiateKind.InvalidCard => Problem(
                title: "Invalid card number",
                detail: "Use a Luhn-valid test card number. Only the last four digits are kept.",
                statusCode: StatusCodes.Status400BadRequest),
            _ => Problem(
                title: "Invalid amount",
                detail: "Amount must be greater than zero with at most two decimal places.",
                statusCode: StatusCodes.Status400BadRequest)
        };
    }

    [HttpGet("/api/3ds/sessions/{id:guid}")]
    [JwtApi]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        if (string.IsNullOrWhiteSpace(actor))
            return Unauthorized();

        var view = await _threeDs.GetAsync(id, actor, cancellationToken).ConfigureAwait(false);
        if (view is null)
            return NotFound();

        return Ok(new
        {
            sessionId = view.SessionId,
            status = view.Status.ToString(),
            amount = view.Amount,
            currency = view.Currency,
            cardLast4 = view.CardLast4,
            eci = view.Eci,
            failureReason = view.FailureReason,
            correlationId = view.CorrelationId,
            createdAt = view.CreatedAt,
            expiresAt = view.ExpiresAt,
            updatedAt = view.UpdatedAt
        });
    }

    /// <summary>
    /// Browser-posted challenge result from the ACS. Anonymous on purpose: trust comes only from the
    /// HMAC signature, the issued-at window and the session state machine, never from a cookie or token.
    /// </summary>
    [HttpPost("/api/3ds/callback")]
    [AllowAnonymous]
    [Consumes("application/x-www-form-urlencoded")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    public async Task<IActionResult> Callback([FromForm] ThreeDsCallbackForm form, CancellationToken cancellationToken)
    {
        if (form is null || !Guid.TryParse(form.SessionId, out var sessionId) || string.IsNullOrWhiteSpace(form.Nonce))
        {
            return Problem(title: "Malformed callback", statusCode: StatusCodes.Status400BadRequest);
        }

        var outcome = await _threeDs.HandleCallbackAsync(
            new AcsChallengeResult(
                sessionId,
                form.TransStatus ?? string.Empty,
                string.IsNullOrEmpty(form.Eci) ? null : form.Eci,
                string.IsNullOrEmpty(form.AuthenticationValue) ? null : form.AuthenticationValue,
                form.IssuedAt,
                form.Nonce,
                form.Signature),
            cancellationToken).ConfigureAwait(false);

        var body = new { sessionId = outcome.SessionId, status = outcome.Status?.ToString(), result = outcome.Kind.ToString() };
        return outcome.Kind switch
        {
            ThreeDsCallbackKind.Applied or ThreeDsCallbackKind.AlreadyApplied => Ok(body),
            ThreeDsCallbackKind.InvalidSignature => Problem(
                title: "Invalid signature",
                detail: "The challenge result is not signed by the ACS. Nothing was changed.",
                statusCode: StatusCodes.Status401Unauthorized),
            ThreeDsCallbackKind.Stale => Problem(
                title: "Stale challenge result",
                detail: "The result is outside the accepted time window. Nothing was changed.",
                statusCode: StatusCodes.Status400BadRequest),
            ThreeDsCallbackKind.NotFound => NotFound(),
            ThreeDsCallbackKind.Expired => StatusCode(StatusCodes.Status410Gone, body),
            _ => Conflict(body)
        };
    }

    private IActionResult ConflictWith(ThreeDsInitiateOutcome outcome)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(
            HttpContext,
            statusCode: StatusCodes.Status409Conflict,
            title: "Conflict",
            detail: "This Idempotency-Key already started a 3-D Secure session.");
        problem.Extensions["sessionId"] = outcome.SessionId;
        problem.Extensions["status"] = outcome.Status.ToString();
        problem.Extensions["acsUrl"] = outcome.AcsUrl;
        return Conflict(problem);
    }
}
