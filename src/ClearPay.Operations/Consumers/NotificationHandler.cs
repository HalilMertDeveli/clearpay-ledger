using System.Globalization;
using System.Text.Json;
using ClearPay.Messaging;
using ClearPay.Operations.Data;
using ClearPay.Operations.Notifications;

namespace ClearPay.Operations.Consumers;

/// <summary>
/// Turns ledger and reconciliation events into customer / operator notifications. Inbox-deduplicated and
/// backed by a unique <c>SourceMessageId</c>, so a redelivered event never notifies twice.
/// </summary>
public sealed class NotificationHandler : IMessageHandler
{
    public const string ConsumerName = "clearpay.ops.notifications";
    public const string OperatorsRecipient = "operations-team";

    private readonly InboxProcessor<OperationsDbContext> _inbox;
    private readonly INotificationSender _sender;
    private readonly TimeProvider _time;

    public NotificationHandler(InboxProcessor<OperationsDbContext> inbox, INotificationSender sender, TimeProvider time)
    {
        _inbox = inbox;
        _sender = sender;
        _time = time;
    }

    public string Consumer => ConsumerName;

    public IReadOnlyCollection<string> RoutingKeys { get; } = new[]
    {
        "clearpay.payment.captured",
        "clearpay.payment.refunded",
        "clearpay.payment.refund_failed",
        "clearpay.payment.declined",
        "clearpay.3ds.failed",
        "clearpay.3ds.timedout",
        "clearpay.reconciliation.completed"
    };

    public async Task<bool> HandleAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        (string Recipient, string Template, string Text)? note;
        try
        {
            note = Compose(message.Type, message.Json());
        }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
        {
            throw new PoisonMessageException($"Unreadable {message.Type} payload", ex);
        }

        if (note is null)
            return false;

        var notification = new Notification
        {
            Id = Guid.NewGuid(),
            SourceMessageId = message.MessageId,
            Recipient = note.Value.Recipient,
            Template = note.Value.Template,
            Text = note.Value.Text,
            CorrelationId = message.CorrelationId,
            CreatedAt = _time.GetUtcNow()
        };

        var processed = await _inbox.ProcessAsync(Consumer, message, (db, _, _) =>
        {
            db.Notifications.Add(notification);
            return Task.CompletedTask;
        }, _time.GetUtcNow(), cancellationToken).ConfigureAwait(false);

        // Delivery happens after the commit; the stored row is the record of what was sent.
        if (processed)
            await _sender.SendAsync(notification, cancellationToken).ConfigureAwait(false);
        return processed;
    }

    internal static (string Recipient, string Template, string Text)? Compose(string type, JsonElement p)
    {
        string Money(string name) => p.GetProperty(name).GetDecimal().ToString("N2", CultureInfo.InvariantCulture) + " TRY";
        string User() => p.GetProperty("userId").GetString() ?? throw new FormatException("userId");

        return type switch
        {
            "clearpay.payment.captured" => (User(), "card-payment-captured",
                $"{Money("amount")} from your card ending {Last4(p)} was added to your wallet."),
            "clearpay.payment.refunded" => (User(), "card-refund",
                $"{Money("amount")} is on its way back to your card ending {Last4(p)}."),
            "clearpay.payment.refund_failed" => (User(), "card-refund-failed",
                $"The refund of {Money("amount")} could not be sent; the amount is back in your wallet."),
            "clearpay.payment.declined" => (User(), "card-payment-declined",
                "Your card payment was declined by the bank. Nothing was charged."),
            "clearpay.3ds.failed" => (User(), "3ds-failed",
                $"Card verification failed for {Money("amount")}. Nothing was charged."),
            "clearpay.3ds.timedout" => (User(), "3ds-timed-out",
                $"Card verification for {Money("amount")} expired. Nothing was charged."),
            "clearpay.reconciliation.completed" => (OperatorsRecipient, "reconciliation-report",
                $"Reconciliation {p.GetProperty("businessDate").GetString()}: {p.GetProperty("matched").GetInt32()} matched, " +
                $"{p.GetProperty("mismatches").GetInt32()} mismatches (run {p.GetProperty("runId").GetGuid()})."),
            _ => null
        };
    }

    private static string Last4(JsonElement p) =>
        p.TryGetProperty("cardLast4", out var l4) && l4.ValueKind == JsonValueKind.String ? l4.GetString()! : "****";
}
