using ClearPay.Application.Ports;
using ClearPay.Messaging;

namespace ClearPay.Infrastructure.Messaging;

/// <summary>
/// Outbox -> RabbitMQ. MessageId = outbox row id, so a re-published row is recognised by consumers'
/// inboxes. Throws when the broker is unreachable or does not confirm, so the outbox keeps the row.
/// </summary>
public sealed class RabbitOutboxPublisher : IOutboxPublisher
{
    private readonly RabbitEventPublisher _publisher;

    public RabbitOutboxPublisher(RabbitEventPublisher publisher)
    {
        _publisher = publisher;
    }

    public Task PublishAsync(Guid messageId, string type, string payload, Guid correlationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _publisher.Publish(messageId, type, payload, correlationId);
        return Task.CompletedTask;
    }
}
