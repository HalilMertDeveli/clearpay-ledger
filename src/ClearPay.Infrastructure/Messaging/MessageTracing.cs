using System.Diagnostics;
using System.Text;

namespace ClearPay.Infrastructure.Messaging;

/// <summary>
/// W3C trace context over AMQP headers (<c>traceparent</c>, <c>tracestate</c>) and the activity source
/// for publish/consume spans. Uses only System.Diagnostics; OpenTelemetry picks the source up by name.
/// </summary>
public static class MessageTracing
{
    public const string SourceName = "ClearPay.Messaging";

    public static readonly ActivitySource Source = new(SourceName);

    public static void Inject(IDictionary<string, object> headers)
    {
        var current = Activity.Current;
        if (current is null || current.IdFormat != ActivityIdFormat.W3C)
            return;
        headers["traceparent"] = current.Id!;
        if (!string.IsNullOrEmpty(current.TraceStateString))
            headers["tracestate"] = current.TraceStateString!;
    }

    public static ActivityContext Extract(IDictionary<string, object>? headers)
    {
        if (headers is null || !headers.TryGetValue("traceparent", out var raw))
            return default;
        var traceparent = raw is byte[] bytes ? Encoding.UTF8.GetString(bytes) : raw?.ToString();
        string? tracestate = null;
        if (headers.TryGetValue("tracestate", out var rawState))
            tracestate = rawState is byte[] s ? Encoding.UTF8.GetString(s) : rawState?.ToString();
        return ActivityContext.TryParse(traceparent, tracestate, isRemote: true, out var context) ? context : default;
    }
}
