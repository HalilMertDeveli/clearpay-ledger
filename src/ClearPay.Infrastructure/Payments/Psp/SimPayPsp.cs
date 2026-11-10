using ClearPay.Application.Payments;
using ClearPay.Domain.Payments;

namespace ClearPay.Infrastructure.Payments.Psp;

/// <summary>
/// Fake payment aggregator that works in minor units (kuruş as integers) with lower-case statuses.
/// Fault trigger: any operation with cents .99 is executed by the PSP but the response is lost
/// (the caller sees a timeout) - the case reconciliation exists for.
/// </summary>
public sealed class SimPayPsp : IPaymentServiceProvider
{
    public const string ProviderName = "simpay";
    private readonly SimulatedPspCore _core;

    public SimPayPsp(SimulatedPspCore core)
    {
        _core = core;
    }

    public string Name => ProviderName;

    public Task<PspResult> AuthorizeAsync(PspAuthorizeRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(new SimPayRequest("payment", ToMinor(request.Amount), null, request.CorrelationId),
            PaymentOperationType.Authorize, request.IdempotencyKey, cancellationToken);

    public Task<PspResult> CaptureAsync(PspFollowUpRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(new SimPayRequest("capture", ToMinor(request.Amount), request.AuthorizationReference, request.CorrelationId),
            PaymentOperationType.Capture, request.IdempotencyKey, cancellationToken);

    public Task<PspResult> RefundAsync(PspFollowUpRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(new SimPayRequest("refund", ToMinor(request.Amount), request.AuthorizationReference, request.CorrelationId),
            PaymentOperationType.Refund, request.IdempotencyKey, cancellationToken);

    public Task<PspResult> VoidAsync(PspFollowUpRequest request, CancellationToken cancellationToken = default) =>
        SendAsync(new SimPayRequest("cancel", ToMinor(request.Amount), request.AuthorizationReference, request.CorrelationId),
            PaymentOperationType.Void, request.IdempotencyKey, cancellationToken);

    public static long ToMinor(decimal amount) => (long)decimal.Round(amount * 100m);

    public static decimal FromMinor(long minor) => minor / 100m;

    private async Task<PspResult> SendAsync(SimPayRequest request, PaymentOperationType type, string key, CancellationToken ct)
    {
        var amount = FromMinor(request.AmountMinor);
        var result = await _core.ExecuteAsync(ProviderName, type, key, request.ParentId, amount, request.Reference, auth =>
        {
            if (auth is not null && amount > auth.Amount)
                return new SimulatedPspCore.Decision(false, "amount_exceeds_parent", false);
            return new SimulatedPspCore.Decision(true, null, LoseResponse: SimulatedPspCore.Cents(amount) == 99);
        }, ct).ConfigureAwait(false);

        var response = new SimPayResponse(
            result.Outcome switch { PspOutcome.Approved => "succeeded", PspOutcome.Declined => "failed", _ => "unknown" },
            result.Reference,
            result.DeclineCode);
        return response.State switch
        {
            "succeeded" => PspResult.Approved(response.Id!),
            "failed" => PspResult.Declined(response.FailureCode ?? "failed"),
            _ => PspResult.TimedOut()
        };
    }

    private sealed record SimPayRequest(string Kind, long AmountMinor, string? ParentId, Guid Reference);

    private sealed record SimPayResponse(string State, string? Id, string? FailureCode);
}
