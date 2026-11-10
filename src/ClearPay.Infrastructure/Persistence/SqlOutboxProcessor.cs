using ClearPay.Application.Ports;
using ClearPay.Domain.Ledger;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPay.Infrastructure.Persistence;

/// <summary>
/// Publishes Pending rows oldest first. A failed publish keeps the row Pending with Attempts + 1 and stops
/// the batch (the broker is most likely down; later rows would fail too and order is kept). After
/// <see cref="MaxAttempts"/> failures the row becomes Failed and waits for an admin re-queue.
/// </summary>
public sealed class SqlOutboxProcessor : IOutboxProcessor
{
    public const int MaxAttempts = 5;

    private readonly ClearPayDbContext _db;
    private readonly IOutboxPublisher _publisher;
    private readonly IClock _clock;
    private readonly ILogger<SqlOutboxProcessor> _logger;

    public SqlOutboxProcessor(ClearPayDbContext db, IOutboxPublisher publisher, IClock clock, ILogger<SqlOutboxProcessor>? logger = null)
    {
        _db = db;
        _publisher = publisher;
        _clock = clock;
        _logger = logger ?? NullLogger<SqlOutboxProcessor>.Instance;
    }

    public async Task<int> ProcessPendingAsync(CancellationToken cancellationToken = default)
    {
        var pending = await _db.OutboxMessages
            .Where(m => m.Status == OutboxStatus.Pending)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
        pending = pending.OrderBy(m => m.OccurredAt).Take(20).ToList();

        var sent = 0;
        foreach (var message in pending)
        {
            // Continue the trace of the request that wrote the row (the job itself has no useful parent).
            System.Diagnostics.ActivityContext.TryParse(message.TraceParent, null, isRemote: true, out var parent);
            using var span = Telemetry.ClearPayTelemetry.Outbox.StartActivity(
                $"outbox {message.Type}", System.Diagnostics.ActivityKind.Producer, parent);
            span?.SetTag("messaging.message.id", message.Id.ToString());
            span?.SetTag("clearpay.correlation_id", message.CorrelationId.ToString());
            span?.SetTag("clearpay.outbox.attempts", message.Attempts);
            try
            {
                await _publisher
                    .PublishAsync(message.Id, message.Type, message.Payload, message.CorrelationId, cancellationToken)
                    .ConfigureAwait(false);
                message.Status = OutboxStatus.Sent;
                message.ProcessedAt = _clock.UtcNow;
                message.LastError = null;
                sent++;
            }
            catch (Exception ex)
            {
                span?.SetStatus(System.Diagnostics.ActivityStatusCode.Error, ex.Message);
                message.Attempts++;
                message.LastError = ex.Message.Length > 400 ? ex.Message[..400] : ex.Message;
                if (message.Attempts >= MaxAttempts)
                {
                    message.Status = OutboxStatus.Failed;
                    message.ProcessedAt = _clock.UtcNow;
                }

                _logger.LogWarning(ex, "Outbox {MessageId} {Type} publish failed (attempt {Attempts}/{Max})",
                    message.Id, message.Type, message.Attempts, MaxAttempts);
                break;
            }
        }

        if (pending.Count > 0)
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return sent;
    }
}
