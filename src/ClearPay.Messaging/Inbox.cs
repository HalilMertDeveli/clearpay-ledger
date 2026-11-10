
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClearPay.Messaging;

/// <summary>"This consumer already handled this message." Primary key (Consumer, MessageId).</summary>
public sealed class InboxMessage
{
    public string Consumer { get; set; } = string.Empty;

    public Guid MessageId { get; set; }

    public string Type { get; set; } = string.Empty;

    public DateTimeOffset ProcessedAt { get; set; }
}

public static class InboxModel
{
    /// <summary>Adds the inbox table to any DbContext (ledger host or a separate service's own database).</summary>
    public static void Apply(ModelBuilder modelBuilder, string? schema = null)
    {
        modelBuilder.Entity<InboxMessage>(b =>
        {
            b.ToTable("InboxMessage", schema);
            b.HasKey(x => new { x.Consumer, x.MessageId });
            b.Property(x => x.Consumer).HasMaxLength(100);
            b.Property(x => x.Type).IsRequired().HasMaxLength(128);
        });
    }
}

/// <summary>
/// Consumer-side idempotency: the inbox row and the handler's changes commit in one transaction.
/// A redelivered message (broker redelivery, outbox re-publish, retry after a crash before ack) finds the
/// row and is acknowledged without running the handler again. Two racing deliveries: the primary key lets
/// exactly one commit; the other rolls back including its side effects.
/// </summary>
public sealed class InboxProcessor<TContext>
    where TContext : DbContext
{
    private readonly TContext _db;
    private readonly ILogger<InboxProcessor<TContext>> _logger;

    public InboxProcessor(TContext db, ILogger<InboxProcessor<TContext>> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>Returns true when the handler ran and committed, false for a duplicate.</summary>
    public async Task<bool> ProcessAsync(
        string consumer,
        InboundMessage message,
        Func<TContext, InboundMessage, CancellationToken, Task> stageChanges,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        var inbox = _db.Set<InboxMessage>();
        if (await inbox.AsNoTracking().AnyAsync(x => x.Consumer == consumer && x.MessageId == message.MessageId, cancellationToken)
                .ConfigureAwait(false))
        {
            _logger.LogInformation("{Consumer} skipped duplicate {Type} {MessageId}", consumer, message.Type, message.MessageId);
            return false;
        }

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        inbox.Add(new InboxMessage { Consumer = consumer, MessageId = message.MessageId, Type = message.Type, ProcessedAt = now });
        await stageChanges(_db, message, cancellationToken).ConfigureAwait(false);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (IsDuplicateKey(ex))
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
            _logger.LogInformation("{Consumer} lost the race for {MessageId}; already processed", consumer, message.MessageId);
            return false;
        }

        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
        return true;
    }

    /// <summary>Provider-neutral: SQL Server "duplicate key" (2627/2601) and SQLite "UNIQUE constraint failed".</summary>
    private static bool IsDuplicateKey(DbUpdateException ex)
    {
        var message = ex.InnerException?.Message ?? ex.Message;
        return message.Contains("duplicate key", StringComparison.OrdinalIgnoreCase)
            || message.Contains("UNIQUE constraint", StringComparison.OrdinalIgnoreCase);
    }
}
