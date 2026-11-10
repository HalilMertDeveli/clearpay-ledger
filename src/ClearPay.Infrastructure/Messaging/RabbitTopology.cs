using RabbitMQ.Client;

namespace ClearPay.Infrastructure.Messaging;

/// <summary>
/// Exchanges and queues, declared idempotently by publisher and consumers.
/// For a consumer named <c>q</c>:
/// <list type="bullet">
/// <item><c>q</c> - work queue bound to the events exchange; a rejected (poison) message dead-letters to <c>q.dlq</c>.</item>
/// <item><c>q.retry.1..n</c> - delay queues with a per-queue TTL; when it expires the message dead-letters back to <c>q</c>.</item>
/// <item><c>q.dlq</c> - parked messages for an operator (no consumer).</item>
/// </list>
/// </summary>
public static class RabbitTopology
{
    public static void DeclareEvents(IModel channel, RabbitOptions options) =>
        channel.ExchangeDeclare(options.EventsExchange, ExchangeType.Topic, durable: true, autoDelete: false);

    public static string RetryQueue(string consumer, int index) => $"{consumer}.retry.{index}";

    public static string DeadLetterQueue(string consumer) => $"{consumer}.dlq";

    public static void DeclareConsumer(IModel channel, RabbitOptions options, string consumer, IEnumerable<string> routingKeys)
    {
        DeclareEvents(channel, options);
        channel.QueueDeclare(DeadLetterQueue(consumer), durable: true, exclusive: false, autoDelete: false);
        channel.QueueDeclare(consumer, durable: true, exclusive: false, autoDelete: false, arguments: new Dictionary<string, object>
        {
            ["x-dead-letter-exchange"] = string.Empty,
            ["x-dead-letter-routing-key"] = DeadLetterQueue(consumer)
        });
        foreach (var key in routingKeys)
            channel.QueueBind(consumer, options.EventsExchange, key);

        for (var i = 0; i < options.RetryDelays.Count; i++)
        {
            channel.QueueDeclare(RetryQueue(consumer, i + 1), durable: true, exclusive: false, autoDelete: false,
                arguments: new Dictionary<string, object>
                {
                    ["x-message-ttl"] = (int)options.RetryDelays[i].TotalMilliseconds,
                    ["x-dead-letter-exchange"] = string.Empty,
                    ["x-dead-letter-routing-key"] = consumer
                });
        }
    }
}
