using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace ClearPay.Messaging;

/// <summary>
/// Publishes one event to the topic exchange (routing key = event type) with publisher confirms and
/// W3C trace headers. Throws when the broker is unreachable or does not confirm in time.
/// </summary>
public sealed class RabbitEventPublisher
{
    private static readonly TimeSpan ConfirmTimeout = TimeSpan.FromSeconds(5);
    private readonly RabbitConnectionProvider _connections;
    private readonly RabbitOptions _options;
    private readonly ILogger<RabbitEventPublisher> _logger;

    public RabbitEventPublisher(RabbitConnectionProvider connections, RabbitOptions options, ILogger<RabbitEventPublisher> logger)
    {
        _connections = connections;
        _options = options;
        _logger = logger;
    }

    public void Publish(Guid messageId, string type, string payload, Guid correlationId)
    {
        using var activity = MessageTracing.Source.StartActivity($"{type} publish", ActivityKind.Producer);
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", _options.EventsExchange);
        activity?.SetTag("messaging.rabbitmq.destination.routing_key", type);
        activity?.SetTag("messaging.message.id", messageId.ToString());
        activity?.SetTag("clearpay.correlation_id", correlationId.ToString());

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
        props.Timestamp = new AmqpTimestamp(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        props.Headers = new Dictionary<string, object>();
        MessageTracing.Inject(props.Headers);

        channel.BasicPublish(_options.EventsExchange, type, mandatory: false, props, Encoding.UTF8.GetBytes(payload ?? string.Empty));
        channel.WaitForConfirmsOrDie(ConfirmTimeout);
        _logger.LogDebug("Published {Type} {MessageId}", type, messageId);
    }
}
