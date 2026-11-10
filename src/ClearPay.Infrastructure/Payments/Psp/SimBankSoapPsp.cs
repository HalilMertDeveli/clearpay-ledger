using System.Globalization;
using System.Security;
using System.Xml.Linq;
using ClearPay.Application.Payments;
using ClearPay.Domain.Payments;

namespace ClearPay.Infrastructure.Payments.Psp;

/// <summary>
/// Fake bank with a SOAP 1.1 style XML interface (TransactionType codes, ResponseCode "00" = approved).
/// Fault trigger: authorize with cents .05 is declined "05" (do not honour).
/// </summary>
public sealed class SimBankSoapPsp : IPaymentServiceProvider
{
    public const string ProviderName = "simbank-soap";
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private static readonly XNamespace Bank = "urn:simbank:pos:v1";
    private readonly SimulatedPspCore _core;

    public SimBankSoapPsp(SimulatedPspCore core)
    {
        _core = core;
    }

    public string Name => ProviderName;

    public Task<PspResult> AuthorizeAsync(PspAuthorizeRequest request, CancellationToken cancellationToken = default) =>
        CallAsync("Auth", PaymentOperationType.Authorize, request.IdempotencyKey, null, request.Amount, request.CorrelationId,
            new XElement(Bank + "Eci", request.Eci), cancellationToken);

    public Task<PspResult> CaptureAsync(PspFollowUpRequest request, CancellationToken cancellationToken = default) =>
        CallAsync("PostAuth", PaymentOperationType.Capture, request.IdempotencyKey, request.AuthorizationReference,
            request.Amount, request.CorrelationId, null, cancellationToken);

    public Task<PspResult> RefundAsync(PspFollowUpRequest request, CancellationToken cancellationToken = default) =>
        CallAsync("Credit", PaymentOperationType.Refund, request.IdempotencyKey, request.AuthorizationReference,
            request.Amount, request.CorrelationId, null, cancellationToken);

    public Task<PspResult> VoidAsync(PspFollowUpRequest request, CancellationToken cancellationToken = default) =>
        CallAsync("Void", PaymentOperationType.Void, request.IdempotencyKey, request.AuthorizationReference,
            request.Amount, request.CorrelationId, null, cancellationToken);

    private async Task<PspResult> CallAsync(
        string transactionType,
        PaymentOperationType type,
        string idempotencyKey,
        string? authRef,
        decimal amount,
        Guid correlationId,
        XElement? extra,
        CancellationToken ct)
    {
        var envelope = new XElement(Soap + "Envelope",
            new XElement(Soap + "Body",
                new XElement(Bank + "PosRequest",
                    new XElement(Bank + "TransactionType", transactionType),
                    new XElement(Bank + "OrderId", correlationId.ToString("N")),
                    new XElement(Bank + "Amount", amount.ToString("F2", CultureInfo.InvariantCulture)),
                    new XElement(Bank + "OrgReference", authRef ?? string.Empty),
                    extra)));
        var wire = envelope.ToString(SaveOptions.DisableFormatting);

        // "Bank" side: parse the envelope.
        var request = XDocument.Parse(wire).Descendants(Bank + "PosRequest").Single();
        var parsedAmount = decimal.Parse(request.Element(Bank + "Amount")!.Value, CultureInfo.InvariantCulture);
        var result = await _core.ExecuteAsync(ProviderName, type, idempotencyKey, authRef, parsedAmount,
            Guid.ParseExact(request.Element(Bank + "OrderId")!.Value, "N"), auth =>
            {
                if (type == PaymentOperationType.Authorize && SimulatedPspCore.Cents(parsedAmount) == 5)
                    return new SimulatedPspCore.Decision(false, "05", false);
                if (auth is not null && parsedAmount > auth.Amount)
                    return new SimulatedPspCore.Decision(false, "13", false);
                return new SimulatedPspCore.Decision(true, null, false);
            }, ct).ConfigureAwait(false);

        var responseXml =
            $"<s:Envelope xmlns:s=\"{Soap}\"><s:Body><b:PosResponse xmlns:b=\"{Bank}\">" +
            $"<b:ResponseCode>{(result.Outcome == PspOutcome.Approved ? "00" : SecurityElement.Escape(result.DeclineCode ?? "96"))}</b:ResponseCode>" +
            $"<b:HostReference>{SecurityElement.Escape(result.Reference ?? string.Empty)}</b:HostReference>" +
            $"<b:TimedOut>{(result.Outcome == PspOutcome.TimedOut ? "true" : "false")}</b:TimedOut>" +
            "</b:PosResponse></s:Body></s:Envelope>";

        // Merchant side: map the XML answer back.
        var response = XDocument.Parse(responseXml).Descendants(Bank + "PosResponse").Single();
        if (response.Element(Bank + "TimedOut")!.Value == "true")
            return PspResult.TimedOut();
        var code = response.Element(Bank + "ResponseCode")!.Value;
        return code == "00"
            ? PspResult.Approved(response.Element(Bank + "HostReference")!.Value)
            : PspResult.Declined(code);
    }
}
