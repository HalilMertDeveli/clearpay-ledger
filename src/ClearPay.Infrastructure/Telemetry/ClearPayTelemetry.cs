using System.Diagnostics;
using System.Diagnostics.Metrics;
using ClearPay.Application.Payments;

namespace ClearPay.Infrastructure.Telemetry;

/// <summary>
/// Spans and counters for the money paths. Plain System.Diagnostics: the host decides whether anything
/// listens (OpenTelemetry in ClearPay.ServiceDefaults); without a listener StartActivity returns null.
/// </summary>
public static class ClearPayTelemetry
{
    public static readonly ActivitySource Payments = new("ClearPay.Payments");
    public static readonly ActivitySource Outbox = new("ClearPay.Outbox");
    public static readonly Meter Meter = new("ClearPay");

    private static readonly Counter<long> PspCalls = Meter.CreateCounter<long>(
        "clearpay.psp.calls", description: "Calls to payment service providers by provider, operation and outcome");

    private static readonly Counter<long> ThreeDsResults = Meter.CreateCounter<long>(
        "clearpay.threeds.results", description: "3-D Secure sessions reaching a final state");

    public static Activity? StartPspCall(string provider, string operation, Guid paymentId, decimal amount)
    {
        var activity = Payments.StartActivity($"psp {provider} {operation}", ActivityKind.Client);
        activity?.SetTag("peer.service", provider);
        activity?.SetTag("clearpay.psp.operation", operation);
        activity?.SetTag("clearpay.payment.id", paymentId.ToString());
        activity?.SetTag("clearpay.amount", (double)amount);
        return activity;
    }

    /// <summary>Records the PSP answer on the span and the counter; declines and timeouts mark the span as an error.</summary>
    public static void Complete(Activity? activity, string provider, string operation, PspResult result)
    {
        PspCalls.Add(1,
            new KeyValuePair<string, object?>("provider", provider),
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("outcome", result.Outcome.ToString()));
        if (activity is null)
            return;
        activity.SetTag("clearpay.psp.outcome", result.Outcome.ToString());
        activity.SetTag("clearpay.psp.reference", result.Reference);
        activity.SetTag("clearpay.psp.decline_code", result.DeclineCode);
        if (result.Outcome != PspOutcome.Approved)
            activity.SetStatus(ActivityStatusCode.Error, $"{result.Outcome} {result.DeclineCode}".Trim());
    }

    public static Activity? StartPayment(string name, Guid? paymentId = null, Guid? correlationId = null)
    {
        var activity = Payments.StartActivity(name, ActivityKind.Internal);
        if (paymentId is not null)
            activity?.SetTag("clearpay.payment.id", paymentId.Value.ToString());
        if (correlationId is not null)
            activity?.SetTag("clearpay.correlation_id", correlationId.Value.ToString());
        return activity;
    }

    public static void ThreeDsFinal(string status) =>
        ThreeDsResults.Add(1, new KeyValuePair<string, object?>("status", status));
}
