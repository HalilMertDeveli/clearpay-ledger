using ClearPay.Application.Ports;
using ClearPay.Domain.Ledger;
using ClearPay.Infrastructure.Persistence;
using ClearPay.Infrastructure.Time;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ClearPay.Tests;

public sealed class OutboxProcessorTests : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly ClearPayDbContext _db;
    private readonly RecordingPublisher _publisher;
    private readonly SqlOutboxProcessor _processor;

    public OutboxProcessorTests()
    {
        _connection = new SqliteConnection("Data Source=:memory:");
        _connection.Open();
        _db = new ClearPayDbContext(new DbContextOptionsBuilder<ClearPayDbContext>().UseSqlite(_connection).Options);
        _db.Database.EnsureCreated();
        _publisher = new RecordingPublisher();
        _processor = new SqlOutboxProcessor(_db, _publisher, new SystemClock());
    }

    [Fact]
    public async Task Pending_is_marked_sent_after_publish_so_timeout_does_not_lose_the_row()
    {
        var id = Guid.NewGuid();
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = id,
            Type = "clearpay.transfer.completed",
            Payload = "{\"ok\":true}",
            CorrelationId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            Status = OutboxStatus.Pending
        });
        await _db.SaveChangesAsync();

        var sent = await _processor.ProcessPendingAsync();
        sent.Should().Be(1);
        _publisher.Count.Should().Be(1);
        (await _db.OutboxMessages.SingleAsync()).Status.Should().Be(OutboxStatus.Sent);
        (await _db.OutboxMessages.SingleAsync()).ProcessedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Publisher_failure_keeps_the_row_pending_until_the_attempt_limit_then_marks_failed()
    {
        _publisher.Fail = true;
        var id = Guid.NewGuid();
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = id,
            Type = "clearpay.topup.timeout",
            Payload = "{}",
            CorrelationId = Guid.NewGuid(),
            OccurredAt = DateTimeOffset.UtcNow,
            Status = OutboxStatus.Pending
        });
        await _db.SaveChangesAsync();

        for (var i = 1; i < SqlOutboxProcessor.MaxAttempts; i++)
        {
            await _processor.ProcessPendingAsync();
            var pending = await _db.OutboxMessages.AsNoTracking().SingleAsync();
            pending.Status.Should().Be(OutboxStatus.Pending);
            pending.Attempts.Should().Be(i);
            pending.LastError.Should().Be("broker down");
        }

        await _processor.ProcessPendingAsync();
        var row = await _db.OutboxMessages.AsNoTracking().SingleAsync();
        row.Status.Should().Be(OutboxStatus.Failed);
        row.Id.Should().Be(id);
    }

    [Fact]
    public async Task Publishes_with_the_outbox_row_id_as_message_id_and_stops_the_batch_on_failure()
    {
        var first = Guid.NewGuid();
        foreach (var (id, offset) in new[] { (first, 0), (Guid.NewGuid(), 1) })
        {
            _db.OutboxMessages.Add(new OutboxMessage
            {
                Id = id, Type = "t", Payload = "{}", CorrelationId = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow.AddSeconds(offset), Status = OutboxStatus.Pending
            });
        }

        await _db.SaveChangesAsync();
        _publisher.FailAfter = 1;

        (await _processor.ProcessPendingAsync()).Should().Be(1);
        _publisher.MessageIds.Should().Equal(first);
        (await _db.OutboxMessages.CountAsync(m => m.Status == OutboxStatus.Pending)).Should().Be(1);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private sealed class RecordingPublisher : IOutboxPublisher
    {
        public int Count { get; private set; }

        public bool Fail { get; set; }

        public int? FailAfter { get; set; }

        public List<Guid> MessageIds { get; } = new();

        public Task PublishAsync(Guid messageId, string type, string payload, Guid correlationId, CancellationToken cancellationToken = default)
        {
            if (Fail || (FailAfter is not null && Count >= FailAfter))
                throw new InvalidOperationException("broker down");
            MessageIds.Add(messageId);
            Count++;
            return Task.CompletedTask;
        }
    }
}
