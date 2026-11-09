using ClearPay.Messaging;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using ClearPay.Domain.Ledger;
using ClearPay.Infrastructure.Messaging;
using ClearPay.Infrastructure.Messaging.Handlers;
using ClearPay.Infrastructure.Persistence;
using ClearPay.Tests.Support;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPay.Tests;

/// <summary>Inbox semantics without a broker (always runs).</summary>
public sealed class InboxProcessorTests : IDisposable
{
    private readonly SqliteConnection _connection = new("Data Source=:memory:");
    private readonly TestClock _clock = new();

    public InboxProcessorTests()
    {
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Same_message_twice_posts_cashback_once()
    {
        var message = Captured(Guid.NewGuid(), "user-a", 250m);

        (await Handler().HandleAsync(message, CancellationToken.None)).Should().BeTrue();
        (await Handler().HandleAsync(message with { Redelivered = true }, CancellationToken.None)).Should().BeFalse();
        (await Handler().HandleAsync(Captured(Guid.NewGuid(), "user-a", 100m), CancellationToken.None)).Should().BeTrue();

        await using var db = NewContext();
        (await db.LedgerEntries.CountAsync(e => e.Kind == LedgerEntryKind.Cashback)).Should().Be(4, "two messages, one pair each");
        (await NetAsync(db, "user-a")).Should().Be(2.50m + 1.00m);
        (await db.Set<InboxMessage>().CountAsync()).Should().Be(2);
    }

    [Fact]
    public async Task Failure_inside_the_handler_rolls_back_the_inbox_row_so_a_retry_runs_again()
    {
        var message = Captured(Guid.NewGuid(), "user-b", 80m);
        await using (var db = NewContext())
        {
            var inbox = new InboxProcessor<ClearPayDbContext>(db, NullLogger<InboxProcessor<ClearPayDbContext>>.Instance);
            var act = () => inbox.ProcessAsync("clearpay.cashback", message, (ctx, _, _) =>
            {
                ctx.AuditLogs.Add(new AuditLog { Id = Guid.NewGuid(), ActorUserId = "x", Action = "half-done", CorrelationId = Guid.NewGuid(), CreatedAt = _clock.UtcNow });
                throw new TimeoutException("downstream timeout");
            }, _clock.UtcNow, CancellationToken.None);
            await act.Should().ThrowAsync<TimeoutException>();
        }

        await using (var db = NewContext())
        {
            (await db.Set<InboxMessage>().CountAsync()).Should().Be(0);
            (await db.AuditLogs.CountAsync()).Should().Be(0);
        }

        (await Handler().HandleAsync(message, CancellationToken.None)).Should().BeTrue();
        await using var check = NewContext();
        (await NetAsync(check, "user-b")).Should().Be(0.80m);
    }

    [Fact]
    public async Task Unreadable_payload_is_poison()
    {
        var bad = new InboundMessage(Guid.NewGuid(), "clearpay.payment.captured", Guid.NewGuid(), "{not json", 1, false);
        var act = () => Handler().HandleAsync(bad, CancellationToken.None);
        await act.Should().ThrowAsync<PoisonMessageException>();
    }

    [Theory]
    [InlineData(250, 2.50)]
    [InlineData(0.99, 0)]
    [InlineData(1, 0.01)]
    [InlineData(10.99, 0.10)]
    public void Cashback_rounds_down_to_kurus(decimal amount, decimal expected) =>
        CashbackOnCaptureHandler.CashbackFor(amount, 1m).Should().Be(expected);

    internal static InboundMessage Captured(Guid id, string user, decimal amount) => new(
        id, "clearpay.payment.captured", Guid.NewGuid(),
        JsonSerializer.Serialize(new { paymentId = Guid.NewGuid(), userId = user, amount }), 1, false);

    internal static async Task<decimal> NetAsync(ClearPayDbContext db, string userId)
    {
        var wallet = await db.Wallets.AsNoTracking().FirstOrDefaultAsync(w => w.UserId == userId);
        if (wallet is null)
            return 0m;
        return (await db.LedgerEntries.AsNoTracking().Where(e => e.WalletId == wallet.Id).Select(e => e.Amount).ToListAsync()).Sum();
    }

    private CashbackOnCaptureHandler Handler()
    {
        var db = NewContext();
        return new CashbackOnCaptureHandler(
            new InboxProcessor<ClearPayDbContext>(db, NullLogger<InboxProcessor<ClearPayDbContext>>.Instance),
            _clock, new NullWalletCache(), new ConfigurationBuilder().AddInMemoryCollection().Build());
    }

    private ClearPayDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ClearPayDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();
}

/// <summary>Against a real RabbitMQ: redelivery, retry with backoff, dead-letter queue.</summary>
public sealed class RabbitMessagingTests
{
    private const string Consumer = CashbackOnCaptureHandler.ConsumerName;

    [RabbitFact]
    public async Task Outbox_republishing_the_same_message_posts_cashback_once()
    {
        await using var host = new RabbitTestHost((s, r) => r.Add<CashbackOnCaptureHandler>());
        await host.StartConsumersAsync();
        var messageId = Guid.NewGuid();
        var payload = JsonSerializer.Serialize(new { paymentId = Guid.NewGuid(), userId = "rmq-user", amount = 300m });

        // At-least-once outbox: the row is published, the process dies before marking it Sent, it is published again.
        await host.Publisher.PublishAsync(messageId, "clearpay.payment.captured", payload, Guid.NewGuid());
        await host.Publisher.PublishAsync(messageId, "clearpay.payment.captured", payload, Guid.NewGuid());

        await RabbitTestHost.WaitUntilAsync(async () =>
        {
            await using var db = host.NewContext();
            return await db.Set<InboxMessage>().AnyAsync() && host.Count(host.Queue(Consumer)) == 0;
        }, TimeSpan.FromSeconds(10));
        await Task.Delay(300);

        await using var check = host.NewContext();
        (await InboxProcessorTests.NetAsync(check, "rmq-user")).Should().Be(3.00m);
        (await check.LedgerEntries.CountAsync(e => e.Kind == LedgerEntryKind.Cashback)).Should().Be(2);
        host.Count(RabbitTopology.DeadLetterQueue(host.Queue(Consumer))).Should().Be(0);
    }

    [RabbitFact]
    public async Task Broker_redelivery_after_a_crash_before_ack_does_not_double_post()
    {
        var recorder = new Recorder();
        await using var host = new RabbitTestHost((s, r) =>
        {
            s.AddSingleton(recorder);
            s.AddScoped<RecordingHandler>();
            r.Add<RecordingHandler>();
        });
        host.DeclareQueues(Consumer, "clearpay.payment.captured");
        var messageId = Guid.NewGuid();
        await host.Publisher.PublishAsync(messageId, "clearpay.payment.captured",
            JsonSerializer.Serialize(new { paymentId = Guid.NewGuid(), userId = "crash-user", amount = 500m }), Guid.NewGuid());
        await RabbitTestHost.WaitUntilAsync(() => Task.FromResult(host.Count(host.Queue(Consumer)) == 1), TimeSpan.FromSeconds(5));

        // First consumer: takes the message, commits the cashback, then "crashes" (channel closes) before acking.
        using (var crashing = host.Connection.CreateModel())
        {
            var delivery = crashing.BasicGet(host.Queue(Consumer), autoAck: false);
            delivery.Should().NotBeNull();
            var handler = host.Resolve<IServiceScopeFactory>().CreateScope().ServiceProvider.GetRequiredService<CashbackOnCaptureHandler>();
            (await handler.HandleAsync(new InboundMessage(Guid.Parse(delivery!.BasicProperties.MessageId), "clearpay.payment.captured",
                Guid.Parse(delivery.BasicProperties.CorrelationId), Encoding.UTF8.GetString(delivery.Body.Span), 1, delivery.Redelivered), CancellationToken.None)).Should().BeTrue();
            crashing.Close();
        }

        // The broker requeues the unacked message; the real consumer gets it with Redelivered = true.
        await host.StartConsumersAsync();
        await RabbitTestHost.WaitUntilAsync(() => Task.FromResult(!recorder.Calls.IsEmpty), TimeSpan.FromSeconds(10));

        recorder.Calls.Should().ContainSingle().Which.Should().Be((messageId, 1, true, false));
        await using var check = host.NewContext();
        (await InboxProcessorTests.NetAsync(check, "crash-user")).Should().Be(5.00m);
        (await check.LedgerEntries.CountAsync(e => e.Kind == LedgerEntryKind.Cashback)).Should().Be(2);
    }

    [RabbitFact]
    public async Task Transient_failures_are_retried_with_growing_delays_then_succeed_once()
    {
        var recorder = new Recorder { FailUntilAttempt = 2 };
        await using var host = new RabbitTestHost((s, r) =>
        {
            s.AddSingleton(recorder);
            s.AddScoped<RecordingHandler>();
            r.Add<RecordingHandler>();
        });
        await host.StartConsumersAsync();
        var messageId = Guid.NewGuid();
        await host.Publisher.PublishAsync(messageId, "clearpay.payment.captured",
            JsonSerializer.Serialize(new { paymentId = Guid.NewGuid(), userId = "flaky-user", amount = 100m }), Guid.NewGuid());

        await RabbitTestHost.WaitUntilAsync(() => Task.FromResult(recorder.Calls.Count >= 3), TimeSpan.FromSeconds(10));

        var calls = recorder.Calls.ToArray();
        calls.Select(c => c.Attempt).Should().Equal(1, 2, 3);
        calls.Last().Processed.Should().BeTrue();
        var stamps = recorder.Stamps.ToArray();
        (stamps[1] - stamps[0]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(180), "first retry waits in the 200 ms queue");
        (stamps[2] - stamps[1]).Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(380), "second retry waits in the 400 ms queue");
        await using var check = host.NewContext();
        (await InboxProcessorTests.NetAsync(check, "flaky-user")).Should().Be(1.00m);
        host.Count(RabbitTopology.DeadLetterQueue(host.Queue(Consumer))).Should().Be(0);
    }

    [RabbitFact]
    public async Task Exhausted_retries_park_the_message_in_the_dead_letter_queue_with_the_error()
    {
        var recorder = new Recorder { FailUntilAttempt = int.MaxValue };
        await using var host = new RabbitTestHost((s, r) =>
        {
            s.AddSingleton(recorder);
            s.AddScoped<RecordingHandler>();
            r.Add<RecordingHandler>();
        }, maxAttempts: 3);
        await host.StartConsumersAsync();
        await host.Publisher.PublishAsync(Guid.NewGuid(), "clearpay.payment.captured",
            JsonSerializer.Serialize(new { paymentId = Guid.NewGuid(), userId = "dead-user", amount = 100m }), Guid.NewGuid());

        var dlq = RabbitTopology.DeadLetterQueue(host.Queue(Consumer));
        await RabbitTestHost.WaitUntilAsync(() => Task.FromResult(host.Count(dlq) == 1), TimeSpan.FromSeconds(10));

        recorder.Calls.Select(c => c.Attempt).Should().Equal(1, 2, 3);
        var parked = host.Get(dlq)!;
        Encoding.UTF8.GetString((byte[])parked.BasicProperties.Headers[RabbitConsumerService.LastErrorHeader])
            .Should().Contain("simulated outage");
        await using var check = host.NewContext();
        (await check.LedgerEntries.CountAsync()).Should().Be(0);
        (await check.Set<InboxMessage>().CountAsync()).Should().Be(0);
    }

    [RabbitFact]
    public async Task Poison_message_goes_straight_to_the_dead_letter_queue()
    {
        await using var host = new RabbitTestHost((s, r) => r.Add<CashbackOnCaptureHandler>());
        await host.StartConsumersAsync();
        await host.Publisher.PublishAsync(Guid.NewGuid(), "clearpay.payment.captured", "{broken", Guid.NewGuid());

        var dlq = RabbitTopology.DeadLetterQueue(host.Queue(Consumer));
        await RabbitTestHost.WaitUntilAsync(() => Task.FromResult(host.Count(dlq) == 1), TimeSpan.FromSeconds(10));

        var parked = host.Get(dlq)!;
        parked.BasicProperties.Headers.Should().ContainKey("x-death", "rejected without requeue -> dead-lettered by the broker");
        parked.BasicProperties.Headers.Should().NotContainKey(RabbitConsumerService.AttemptHeader);
    }

    private sealed class Recorder
    {
        public int FailUntilAttempt { get; init; }

        public ConcurrentQueue<(Guid MessageId, int Attempt, bool Redelivered, bool Processed)> Calls { get; } = new();

        public ConcurrentQueue<DateTime> Stamps { get; } = new();
    }

    /// <summary>Wraps the real cashback handler; can fail the first attempts like a flaky dependency.</summary>
    private sealed class RecordingHandler : IMessageHandler
    {
        private readonly CashbackOnCaptureHandler _inner;
        private readonly Recorder _recorder;

        public RecordingHandler(CashbackOnCaptureHandler inner, Recorder recorder)
        {
            _inner = inner;
            _recorder = recorder;
        }

        public string Consumer => _inner.Consumer;

        public IReadOnlyCollection<string> RoutingKeys => _inner.RoutingKeys;

        public async Task<bool> HandleAsync(InboundMessage message, CancellationToken cancellationToken)
        {
            _recorder.Stamps.Enqueue(DateTime.UtcNow);
            if (message.Attempt <= _recorder.FailUntilAttempt)
            {
                _recorder.Calls.Enqueue((message.MessageId, message.Attempt, message.Redelivered, false));
                throw new TimeoutException("simulated outage");
            }

            var processed = await _inner.HandleAsync(message, cancellationToken);
            _recorder.Calls.Enqueue((message.MessageId, message.Attempt, message.Redelivered, processed));
            return processed;
        }
    }
}
