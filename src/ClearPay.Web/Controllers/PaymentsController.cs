using System.Security.Claims;
using ClearPay.Application.Payments;
using ClearPay.Application.Ports;
using ClearPay.Web.Api;
using Microsoft.AspNetCore.Mvc;

namespace ClearPay.Web.Controllers;

/// <summary>
/// Card payments through the fake PSP adapters. Every POST needs an <c>Idempotency-Key</c>.
/// 201/200 done, 202 PSP timed out (retry with the same key), 409 replay, 402 declined,
/// 422 business rule, 404 not yours / unknown.
/// </summary>
[ApiController]
[JwtApi]
public sealed class PaymentsController : ControllerBase
{
    private readonly ICardPaymentService _payments;
    private readonly IPspRouter _router;

    public PaymentsController(ICardPaymentService payments, IPspRouter router)
    {
        _payments = payments;
        _router = router;
    }

    [HttpGet("/api/payments/providers")]
    public IActionResult Providers() => Ok(_router.Names.OrderBy(n => n));

    [HttpPost("/api/payments")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status402PaymentRequired)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status409Conflict)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> Create([FromBody] CreatePaymentApiRequest body, CancellationToken cancellationToken)
    {
        var actor = Actor();
        if (actor is null)
            return Unauthorized();
        var result = await _payments.CreateAsync(
            new CreateCardPaymentCommand(actor, body?.ThreeDsSessionId ?? Guid.Empty, body?.Provider, body?.Capture ?? true, Key()),
            cancellationToken).ConfigureAwait(false);
        return Map(result, created: true);
    }

    [HttpPost("/api/payments/{id:guid}/capture")]
    public async Task<IActionResult> Capture(Guid id, CancellationToken cancellationToken) =>
        await Act(id, null, _payments.CaptureAsync, cancellationToken).ConfigureAwait(false);

    [HttpPost("/api/payments/{id:guid}/void")]
    public async Task<IActionResult> Void(Guid id, CancellationToken cancellationToken) =>
        await Act(id, null, _payments.VoidAsync, cancellationToken).ConfigureAwait(false);

    [HttpPost("/api/payments/{id:guid}/refunds")]
    public async Task<IActionResult> Refund(Guid id, [FromBody] RefundApiRequest body, CancellationToken cancellationToken) =>
        await Act(id, body?.Amount ?? 0m, _payments.RefundAsync, cancellationToken).ConfigureAwait(false);

    [HttpGet("/api/payments/{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken cancellationToken)
    {
        var actor = Actor();
        if (actor is null)
            return Unauthorized();
        var view = await _payments.GetAsync(id, actor, cancellationToken).ConfigureAwait(false);
        return view is null ? NotFound() : Ok(Body(view, null));
    }

    private async Task<IActionResult> Act(
        Guid id,
        decimal? amount,
        Func<PaymentActionCommand, CancellationToken, Task<PaymentResult>> action,
        CancellationToken cancellationToken)
    {
        var actor = Actor();
        if (actor is null)
            return Unauthorized();
        var result = await action(new PaymentActionCommand(actor, id, amount, Key()), cancellationToken).ConfigureAwait(false);
        return Map(result, created: false);
    }

    private IActionResult Map(PaymentResult result, bool created)
    {
        return result.Kind switch
        {
            PaymentResultKind.Ok when created => Created($"/api/payments/{result.Payment!.PaymentId}", Body(result.Payment, result.OperationId)),
            PaymentResultKind.Ok => Ok(Body(result.Payment!, result.OperationId)),
            PaymentResultKind.TimedOut => Accepted(Body(result.Payment!, result.OperationId)),
            PaymentResultKind.Replay => ProblemWith(StatusCodes.Status409Conflict, "Already processed",
                "This Idempotency-Key was already used; nothing was executed again.", result),
            PaymentResultKind.KeyPayloadMismatch => ProblemWith(StatusCodes.Status409Conflict, "Idempotency-Key reused",
                "The key was used with a different request.", result),
            PaymentResultKind.MissingKey => ProblemWith(StatusCodes.Status400BadRequest, "Idempotency-Key required",
                "Send the Idempotency-Key header.", result),
            PaymentResultKind.InvalidAmount => ProblemWith(StatusCodes.Status400BadRequest, "Invalid amount",
                "Amount must be greater than zero with at most two decimal places.", result),
            PaymentResultKind.NotFound => NotFound(),
            PaymentResultKind.UnknownProvider => ProblemWith(StatusCodes.Status400BadRequest, "Unknown provider",
                "Use one of GET /api/payments/providers.", result),
            PaymentResultKind.Declined => ProblemWith(StatusCodes.Status402PaymentRequired, "Declined by the provider",
                "The PSP declined the operation. The ledger is unchanged.", result),
            PaymentResultKind.ThreeDsNotAuthenticated => ProblemWith(StatusCodes.Status422UnprocessableEntity, "3-D Secure not authenticated",
                "The 3-D Secure session must be Authenticated.", result),
            PaymentResultKind.ThreeDsAlreadyUsed => ProblemWith(StatusCodes.Status409Conflict, "3-D Secure already used",
                "One authentication pays for one payment.", result),
            PaymentResultKind.ExceedsRefundable => ProblemWith(StatusCodes.Status422UnprocessableEntity, "Refund too large",
                "Refunds cannot exceed the captured amount minus earlier refunds.", result),
            PaymentResultKind.InsufficientFunds => ProblemWith(StatusCodes.Status422UnprocessableEntity, "Insufficient funds",
                "The wallet no longer holds enough to return this amount to the card.", result),
            _ => ProblemWith(StatusCodes.Status422UnprocessableEntity, "Invalid state",
                "The payment is not in a state that allows this operation.", result)
        };
    }

    private ObjectResult ProblemWith(int status, string title, string detail, PaymentResult result)
    {
        var problem = ProblemDetailsFactory.CreateProblemDetails(HttpContext, statusCode: status, title: title, detail: detail);
        if (result.Payment is not null)
        {
            problem.Extensions["paymentId"] = result.Payment.PaymentId;
            problem.Extensions["status"] = result.Payment.Status.ToString();
        }

        if (result.DeclineCode is not null)
            problem.Extensions["declineCode"] = result.DeclineCode;
        return new ObjectResult(problem) { StatusCode = status, ContentTypes = { "application/problem+json" } };
    }

    private static object Body(CardPaymentView p, Guid? operationId) => new
    {
        paymentId = p.PaymentId,
        operationId,
        status = p.Status.ToString(),
        provider = p.Provider,
        providerReference = p.ProviderReference,
        amount = p.Amount,
        capturedAmount = p.CapturedAmount,
        refundedAmount = p.RefundedAmount,
        cardLast4 = p.CardLast4,
        correlationId = p.CorrelationId,
        operations = p.Operations.Select(o => new
        {
            operationId = o.OperationId,
            type = o.Type.ToString(),
            status = o.Status.ToString(),
            amount = o.Amount,
            providerReference = o.ProviderReference,
            createdAt = o.CreatedAt
        })
    };

    private string? Actor()
    {
        var actor = User.FindFirstValue(ClaimTypes.NameIdentifier);
        return string.IsNullOrWhiteSpace(actor) ? null : actor;
    }

    private string Key() => Request.Headers["Idempotency-Key"].ToString();
}
