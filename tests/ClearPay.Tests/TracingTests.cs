using System.Diagnostics;
using ClearPay.Application.Ports;
using ClearPay.Domain.Ledger;
using ClearPay.Infrastructure.Persistence;
using ClearPay.Messaging;
using ClearPay.Tests.Support;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace ClearPay.Tests;

/// <summary>
/// One payment = one trace across services: the request's trace is stored on the outbox row, the outbox
/// worker continues it when it publishes, and the AMQP headers carry it to the consumer.
/// </summary>
[Collection("tracing")] // ActivityListener is process-wide
public sealed class TracingTests : IDisposable
{
    private static readonly ActivitySource Request = new("ClearPay.Tests.Request");
    private readonly ActivityListener _listener;
    private readonly List<Activity> _stopped = new();
    private readonly SqliteConnection _connection = new("Data Source=:memory:");

    public TracingTests()
    {
        _listener = new ActivityListener
        {
            ShouldListenTo = s => s.Name.StartsWith("ClearPay", StringComparison.Ordinal),
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = a => { lock (_stopped) _stopped.Add(a); }
        };
        ActivitySource.AddActivityListener(_listener);
        _connection.Open();
        using var db = NewContext();
        db.Database.EnsureCreated();
    }

    [Fact]
    public async Task Outbox_publish_continues_the_trace_of_the_request_that_wrote_the_row()
    {
        string requestTraceId;
        using (var request = Request.StartActivity("POST /api/payments", ActivityKind.Server)!)
        {
            requestTraceId = request.TraceId.ToString();
            await using var db = NewContext();
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = Guid.NewGuid(), Type = "clearpay.payment.captured", Payload = "{}", CorrelationId = Guid.NewGuid(),
                OccurredAt = DateTimeOffset.UtcNow, Status = OutboxStatus.Pending
            });
            await db.SaveChangesAsync();
        }

        await using (var db = NewContext())
            (await db.OutboxMessages.SingleAsync()).TraceParent.Should().Contain(requestTraceId);

        // Later, outside the request (Hangfire job): no ambient activity.
        Activity.Current = null;
        var publisher = new CapturingPublisher();
        await using (var db = NewContext())
            (await new SqlOutboxProcessor(db, publisher, new TestClock()).ProcessPendingAsync()).Should().Be(1);

        publisher.SeenTraceId.Should().Be(requestTraceId, "the publish happens inside a span of the original trace");
        var outboxSpan = _stopped.Single(a => a.Source.Name == "ClearPay.Outbox");
        outboxSpan.Kind.Should().Be(ActivityKind.Producer);
        outboxSpan.DisplayName.Should().Be("outbox clearpay.payment.captured");
        outboxSpan.TraceId.ToString().Should().Be(requestTraceId);
    }

    [Fact]
    public void Amqp_headers_carry_the_trace_to_the_consumer()
    {
        using var producer = Request.StartActivity("publish", ActivityKind.Producer)!;
        var headers = new Dictionary<string, object>();
        MessageTracing.Inject(headers);

        // RabbitMQ hands header values back as byte[].
        var received = headers.ToDictionary(h => h.Key, h => (object)System.Text.Encoding.UTF8.GetBytes((string)h.Value));
        var context = MessageTracing.Extract(received);

        context.TraceId.Should().Be(producer.TraceId);
        context.SpanId.Should().Be(producer.SpanId);
        context.IsRemote.Should().BeTrue();
        MessageTracing.Extract(new Dictionary<string, object>()).Should().Be(default(ActivityContext));
    }

    private ClearPayDbContext NewContext() =>
        new(new DbContextOptionsBuilder<ClearPayDbContext>().UseSqlite(_connection).Options);

    public void Dispose()
    {
        _listener.Dispose();
        _connection.Dispose();
    }

    private sealed class CapturingPublisher : IOutboxPublisher
    {
        public string? SeenTraceId { get; private set; }

        public Task PublishAsync(Guid messageId, string type, string payload, Guid correlationId, CancellationToken cancellationToken = default)
        {
            SeenTraceId = Activity.Current?.TraceId.ToString();
            return Task.CompletedTask;
        }
    }
}
