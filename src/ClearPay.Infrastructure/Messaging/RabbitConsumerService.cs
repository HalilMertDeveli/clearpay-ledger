using System.Diagnostics;
using System.Text;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using RabbitMQ.Client;
using RabbitMQ.Client.Events;

namespace ClearPay.Infrastructure.Messaging;

/// <summary>Which handler types this process consumes (each gets its own queue and channel).</summary>
public sealed class MessageHandlerRegistry
{
    private readonly List<Type> _types = new();

    public IReadOnlyList<Type> HandlerTypes => _types;

    public MessageHandlerRegistry Add<THandler>()
        where THandler : IMessageHandler
    {
        _types.Add(typeof(THandler));
        return this;
    }
}

/// <summary>
/// Runs every registered <see cref="IMessageHandler"/> against RabbitMQ with manual acks:
/// <list type="bullet">
/// <item>success -> ack;</item>
/// <item>exception -> copy to the next delay queue (<c>x-attempt</c> + 1, TTL backoff), ack the original;</item>
/// <item>attempts exhausted -> copy to <c>.dlq</c> with <c>x-last-error</c>, ack;</item>
/// <item><see cref="PoisonMessageException"/> or no MessageId -> reject without requeue (dead-letters to <c>.dlq</c>).</item>
/// </list>
/// A crash between the handler's commit and the ack makes the broker redeliver; the inbox turns that into a no-op.
/// </summary>
public sealed class RabbitConsumerService : BackgroundService
{
    public const string AttemptHeader = "x-attempt";
    public const string LastErrorHeader = "x-last-error";

    private readonly IServiceScopeFactory _scopes;
    private readonly RabbitConnectionProvider _connections;
    private readonly RabbitOptions _options;
    private readonly MessageHandlerRegistry _registry;
    private readonly ILogger<RabbitConsumerService> _logger;
    private readonly List<IModel> _channels = new();

    public RabbitConsumerService(
        IServiceScopeFactory scopes,
        RabbitConnectionProvider connections,
        RabbitOptions options,
        MessageHandlerRegistry registry,
        ILogger<RabbitConsumerService> logger)
    {
        _scopes = scopes;
        _connections = connections;
        _options = options;
        _registry = registry;
        _logger = logger;
    }

    /// <summary>Set once every consumer is attached (tests wait on it).</summary>
    public Task Started => _started.Task;

    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled || !_options.ConsumersEnabled || _registry.HandlerTypes.Count == 0)
            return;

        IConnection? connection = null;
        while (!stoppingToken.IsCancellationRequested && (connection = _connections.TryGet()) is null)
            await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken).ConfigureAwait(false);
        if (connection is null)
            return;

        foreach (var type in _registry.HandlerTypes)
        {
            string consumer;
            IReadOnlyCollection<string> keys;
            using (var scope = _scopes.CreateScope())
            {
                var probe = (IMessageHandler)scope.ServiceProvider.GetRequiredService(type);
                consumer = _options.QueuePrefix + probe.Consumer;
                keys = probe.RoutingKeys;
            }

            var channel = connection.CreateModel();
            _channels.Add(channel);
            RabbitTopology.DeclareConsumer(channel, _options, consumer, keys);
            channel.BasicQos(0, _options.Prefetch, global: false);
            var basic = new AsyncEventingBasicConsumer(channel);
            basic.Received += (_, delivery) => OnReceivedAsync(channel, type, consumer, delivery, stoppingToken);
            channel.BasicConsume(consumer, autoAck: false, basic);
            _logger.LogInformation("Consuming {Queue} ({Keys})", consumer, string.Join(", ", keys));
        }

        _started.TrySetResult();
        try
        {
            await Task.Delay(Timeout.Infinite, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private async Task OnReceivedAsync(IModel channel, Type handlerType, string consumer, BasicDeliverEventArgs delivery, CancellationToken ct)
    {
        var props = delivery.BasicProperties;
        var attempt = ReadAttempt(props.Headers);
        using var activity = MessageTracing.Source.StartActivity(
            $"{consumer} process", ActivityKind.Consumer, MessageTracing.Extract(props.Headers));
        activity?.SetTag("messaging.system", "rabbitmq");
        activity?.SetTag("messaging.destination.name", consumer);
        activity?.SetTag("messaging.message.id", props.MessageId);
        activity?.SetTag("messaging.rabbitmq.attempt", attempt);

        if (!Guid.TryParse(props.MessageId, out var messageId))
        {
            _logger.LogError("{Consumer}: message without a valid MessageId dead-lettered", consumer);
            channel.BasicReject(delivery.DeliveryTag, requeue: false);
            return;
        }

        var message = new InboundMessage(
            messageId,
            props.Type ?? delivery.RoutingKey,
            Guid.TryParse(props.CorrelationId, out var correlation) ? correlation : Guid.Empty,
            Encoding.UTF8.GetString(delivery.Body.Span),
            attempt,
            delivery.Redelivered);

        try
        {
            using var scope = _scopes.CreateScope();
            var handler = (IMessageHandler)scope.ServiceProvider.GetRequiredService(handlerType);
            var processed = await handler.HandleAsync(message, ct).ConfigureAwait(false);
            activity?.SetTag("clearpay.inbox.duplicate", !processed);
            channel.BasicAck(delivery.DeliveryTag, multiple: false);
        }
        catch (PoisonMessageException ex)
        {
            _logger.LogError(ex, "{Consumer}: poison message {MessageId} dead-lettered", consumer, messageId);
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            channel.BasicReject(delivery.DeliveryTag, requeue: false);
        }
        catch (Exception ex)
        {
            activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
            var copy = channel.CreateBasicProperties();
            copy.Persistent = true;
            copy.MessageId = props.MessageId;
            copy.Type = props.Type;
            copy.CorrelationId = props.CorrelationId;
            copy.ContentType = props.ContentType;
            copy.Headers = props.Headers is null ? new Dictionary<string, object>() : new Dictionary<string, object>(props.Headers);
            copy.Headers[AttemptHeader] = attempt + 1;
            copy.Headers[LastErrorHeader] = Truncate(ex.GetType().Name + ": " + ex.Message, 500);

            if (attempt < _options.MaxAttempts && _options.RetryDelays.Count > 0)
            {
                var index = Math.Min(attempt, _options.RetryDelays.Count);
                _logger.LogWarning(ex, "{Consumer}: attempt {Attempt} of {Max} failed for {MessageId}; retry in {Delay}",
                    consumer, attempt, _options.MaxAttempts, messageId, _options.RetryDelays[index - 1]);
                channel.BasicPublish(string.Empty, RabbitTopology.RetryQueue(consumer, index), copy, delivery.Body);
            }
            else
            {
                _logger.LogError(ex, "{Consumer}: {MessageId} failed {Attempt} times; moved to {Queue}",
                    consumer, messageId, attempt, RabbitTopology.DeadLetterQueue(consumer));
                channel.BasicPublish(string.Empty, RabbitTopology.DeadLetterQueue(consumer), copy, delivery.Body);
            }

            channel.BasicAck(delivery.DeliveryTag, multiple: false);
        }
    }

    private static int ReadAttempt(IDictionary<string, object>? headers)
    {
        if (headers is null || !headers.TryGetValue(AttemptHeader, out var raw))
            return 1;
        return raw switch
        {
            int i => i,
            long l => (int)l,
            byte[] b when int.TryParse(Encoding.UTF8.GetString(b), out var parsed) => parsed,
            _ => 1
        };
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    public override void Dispose()
    {
        foreach (var channel in _channels)
        {
            try
            {
                channel.Close();
            }
            catch (Exception)
            {
                // Connection already gone.
            }

            channel.Dispose();
        }

        base.Dispose();
    }
}
