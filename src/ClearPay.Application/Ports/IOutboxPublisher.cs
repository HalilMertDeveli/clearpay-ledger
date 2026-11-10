namespace ClearPay.Application.Ports;

/// <summary>
/// Delivers one outbox row. <paramref name="messageId"/> is the outbox row id: it stays the same when
/// the row is published again, so consumers can deduplicate (inbox table) on it.
/// Must throw when delivery is not confirmed; the row then stays in the outbox for the next run.
/// </summary>
public interface IOutboxPublisher
{
    Task PublishAsync(Guid messageId, string type, string payload, Guid correlationId, CancellationToken cancellationToken = default);
}

public interface IOutboxProcessor
{
    Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default);
}
