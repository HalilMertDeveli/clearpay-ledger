using ClearPay.Operations.Data;

namespace ClearPay.Operations.Notifications;

/// <summary>Channel adapter (e-mail, SMS, push). The demo only has a log-backed mailbox.</summary>
public interface INotificationSender
{
    Task SendAsync(Notification notification, CancellationToken cancellationToken);
}

/// <summary>Writes the notification to the structured log; the row in ops.Notification is the "mailbox".</summary>
public sealed class LogNotificationSender : INotificationSender
{
    private readonly ILogger<LogNotificationSender> _logger;

    public LogNotificationSender(ILogger<LogNotificationSender> logger)
    {
        _logger = logger;
    }

    public Task SendAsync(Notification notification, CancellationToken cancellationToken)
    {
        _logger.LogInformation(
            "Notification {Template} to {Recipient} via {Channel} (correlation {CorrelationId}): {Text}",
            notification.Template, notification.Recipient, notification.Channel, notification.CorrelationId, notification.Text);
        return Task.CompletedTask;
    }
}
