using System.Text.Json;

namespace ClearPay.Messaging;

/// <summary>A message as the consumer sees it, independent of RabbitMQ types.</summary>
public sealed record InboundMessage(
    Guid MessageId,
    string Type,
    Guid CorrelationId,
    string Payload,
    int Attempt,
    bool Redelivered)
{
    public JsonElement Json() => JsonDocument.Parse(Payload).RootElement.Clone();
}

/// <summary>Thrown for messages that can never succeed (bad payload): they go straight to the dead-letter queue.</summary>
public sealed class PoisonMessageException : Exception
{
    public PoisonMessageException(string message, Exception? inner = null)
        : base(message, inner)
    {
    }
}

/// <summary>
/// One consumer (= one queue). Implementations run inside a DI scope; <see cref="HandleAsync"/> is called at
/// most once per MessageId thanks to the inbox, and any exception means "retry later".
/// </summary>
public interface IMessageHandler
{
    /// <summary>Queue name, also the inbox consumer name.</summary>
    string Consumer { get; }

    IReadOnlyCollection<string> RoutingKeys { get; }

    /// <summary>Returns true when the message was processed, false when it was a duplicate.</summary>
    Task<bool> HandleAsync(InboundMessage message, CancellationToken cancellationToken);
}
