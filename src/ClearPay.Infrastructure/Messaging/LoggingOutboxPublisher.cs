using ClearPay.Application.Ports;
using Microsoft.Extensions.Logging;

namespace ClearPay.Infrastructure.Messaging;

/// <summary>
/// Used only when RabbitMQ is switched off (empty <c>ConnectionStrings:RabbitMq</c>): the row is logged and
/// marked Sent. When RabbitMQ is configured but down, <see cref="RabbitOutboxPublisher"/> throws instead,
/// so no event is silently lost.
/// </summary>
public sealed class LoggingOutboxPublisher : IOutboxPublisher
{
    private readonly ILogger<LoggingOutboxPublisher> _logger;

    public LoggingOutboxPublisher(ILogger<LoggingOutboxPublisher> logger)
    {
        _logger = logger;
    }

    public Task PublishAsync(Guid messageId, string type, string payload, Guid correlationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogInformation(
            "Outbox {MessageId} {Type} correlation {CorrelationId} payload length {Length} (RabbitMQ off)",
            messageId,
            type,
            correlationId,
            payload?.Length ?? 0);
        return Task.CompletedTask;
    }
}
