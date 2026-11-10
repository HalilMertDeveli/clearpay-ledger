using System.Text;
using ClearPay.Application.Ports;
using Microsoft.Extensions.Logging;

namespace ClearPay.Infrastructure.Messaging;

/// <summary>
/// Publishes an outbox row to the <c>clearpay.events</c> topic exchange (routing key = event type) with
/// publisher confirms. MessageId = outbox row id. Throws when the broker is unreachable or does not
/// confirm, so the outbox keeps the row and retries it.
/// </summary>
public sealed class RabbitOutboxPublisher : IOutboxPublisher
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);
    private readonly RabbitConnectionProvider _connections;
    private readonly RabbitOptions _options;
    private readonly ILogger<RabbitOutboxPublisher> _logger;

    public RabbitOutboxPublisher(RabbitConnectionProvider connections, RabbitOptions options, ILogger<RabbitOutboxPublisher> logger)
    {
        _connections = connections;
        _options = options;
        _logger = logger;
    }

    public Task PublishAsync(Guid messageId, string type, string payload, Guid correlationId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var connection = _connections.Get();
        using var channel = connection.CreateModel();
        channel.ConfirmSelect();
        RabbitTopology.DeclareEvents(channel, _options);

        var props = channel.CreateBasicProperties();
        props.Persistent = true;
        props.MessageId = messageId.ToString();
        props.Type = type;
        props.CorrelationId = correlationId.ToString();
        props.ContentType = "application/json";
        props.Timestamp = new RabbitMQ.Client.AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        props.Headers = new Dictionary<string, object>();
        MessageTracing.Inject(props.Headers);

        channel.BasicPublish(_options.EventsExchange, type, mandatory: false, props, Encoding.UTF8.GetBytes(payload ?? string.Empty));
        channel.WaitForConfirmsOrDie(ConfirmTimeout);
        _logger.LogDebug("Published {Type} {MessageId}", type, messageId);
        return Task.CompletedTask;
    }
}
