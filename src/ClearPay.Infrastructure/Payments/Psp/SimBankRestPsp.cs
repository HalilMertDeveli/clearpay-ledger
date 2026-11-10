using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ClearPay.Application.Payments;
using ClearPay.Domain.Payments;

namespace ClearPay.Infrastructure.Payments.Psp;

/// <summary>
/// Fake acquiring bank with a JSON API (decimal strings, upper-case statuses).
/// Fault triggers: authorize with cents .51 is declined "51" (insufficient funds);
/// refund with cents .52 is declined "R52".
/// </summary>
public sealed class SimBankRestPsp : IPaymentServiceProvider
{
    public const string ProviderName = "simbank-rest";
    private readonly SimulatedPspCore _core;

    public SimBankRestPsp(SimulatedPspCore core)
    {
        _core = core;
    }

    public string Name => ProviderName;

    public Task<PspResult> AuthorizeAsync(PspAuthorizeRequest request, CancellationToken cancellationToken = default)
    {
        var json = new JsonObject
        {
            ["merchantRef"] = request.CorrelationId.ToString("N"),
            ["amount"] = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            ["currency"] = request.Currency,
            ["card"] = new JsonObject { ["last4"] = request.CardLast4 },
            ["threeDS"] = new JsonObject { ["eci"] = request.Eci, ["cavv"] = request.AuthenticationValue }
        };
        return PostAsync("/v1/authorizations", PaymentOperationType.Authorize, request.IdempotencyKey, null, json, cancellationToken);
    }

    public Task<PspResult> CaptureAsync(PspFollowUpRequest request, CancellationToken cancellationToken = default) =>
        FollowUpAsync("/v1/captures", PaymentOperationType.Capture, request, cancellationToken);

    public Task<PspResult> RefundAsync(PspFollowUpRequest request, CancellationToken cancellationToken = default) =>
        FollowUpAsync("/v1/refunds", PaymentOperationType.Refund, request, cancellationToken);

    public Task<PspResult> VoidAsync(PspFollowUpRequest request, CancellationToken cancellationToken = default) =>
        FollowUpAsync("/v1/voids", PaymentOperationType.Void, request, cancellationToken);

    private Task<PspResult> FollowUpAsync(string path, PaymentOperationType type, PspFollowUpRequest request, CancellationToken ct)
    {
        var json = new JsonObject
        {
            ["authorizationId"] = request.AuthorizationReference,
            ["amount"] = request.Amount.ToString("F2", CultureInfo.InvariantCulture),
            ["merchantRef"] = request.CorrelationId.ToString("N")
        };
        return PostAsync(path, type, request.IdempotencyKey, request.AuthorizationReference, json, ct);
    }

    /// <summary>In-process stand-in for an HTTPS POST: the "bank" parses the JSON body and answers in JSON.</summary>
    private async Task<PspResult> PostAsync(
        string path, PaymentOperationType type, string idempotencyKey, string? authRef, JsonObject body, CancellationToken ct)
    {
        var wire = body.ToJsonString();
        var parsed = JsonNode.Parse(wire)!;
        var amount = decimal.Parse(parsed["amount"]!.GetValue<string>(), CultureInfo.InvariantCulture);
        var correlation = Guid.ParseExact(parsed["merchantRef"]!.GetValue<string>(), "N");

        var result = await _core.ExecuteAsync(ProviderName, type, idempotencyKey, authRef, amount, correlation, auth =>
        {
            var cents = SimulatedPspCore.Cents(amount);
            if (type == PaymentOperationType.Authorize && cents == 51)
                return new SimulatedPspCore.Decision(false, "51", false);
            if (type == PaymentOperationType.Refund && cents == 52)
                return new SimulatedPspCore.Decision(false, "R52", false);
            if (auth is not null && amount > auth.Amount)
                return new SimulatedPspCore.Decision(false, "13", false);
            return new SimulatedPspCore.Decision(true, null, false);
        }, ct).ConfigureAwait(false);

        // Response leg: serialize as the bank would, then map back.
        var response = JsonSerializer.Serialize(new
        {
            path,
            status = result.Outcome switch { PspOutcome.Approved => "APPROVED", PspOutcome.Declined => "DECLINED", _ => "TIMEOUT" },
            id = result.Reference,
            responseCode = result.DeclineCode
        });
        using var doc = JsonDocument.Parse(response);
        var status = doc.RootElement.GetProperty("status").GetString();
        return status switch
        {
            "APPROVED" => PspResult.Approved(doc.RootElement.GetProperty("id").GetString()!),
            "DECLINED" => PspResult.Declined(doc.RootElement.GetProperty("responseCode").GetString() ?? "declined"),
            _ => PspResult.TimedOut()
        };
    }
}
