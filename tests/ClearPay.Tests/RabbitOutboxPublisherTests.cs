using ClearPay.Infrastructure.Messaging;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;

namespace ClearPay.Tests;

public sealed class RabbitOutboxPublisherTests
{
    [Fact]
    public async Task Configured_but_unreachable_broker_throws_so_the_outbox_keeps_the_row()
    {
        var options = new RabbitOptions { Uri = "amqp://guest:guest@127.0.0.1:1/" };
        using var connections = new RabbitConnectionProvider(options, NullLogger<RabbitConnectionProvider>.Instance);
        var sut = new RabbitOutboxPublisher(connections, options, NullLogger<RabbitOutboxPublisher>.Instance);

        var act = async () => await sut.PublishAsync(Guid.NewGuid(), "clearpay.transfer.completed", "{}", Guid.NewGuid());

        await act.Should().ThrowAsync<InvalidOperationException>();
        connections.Status.Should().Be("down");
    }

    [Fact]
    public async Task Switched_off_broker_uses_the_log_publisher()
    {
        var options = new RabbitOptions { Uri = "" };
        using var connections = new RabbitConnectionProvider(options, NullLogger<RabbitConnectionProvider>.Instance);
        connections.Status.Should().Be("off");

        var log = new LoggingOutboxPublisher(NullLogger<LoggingOutboxPublisher>.Instance);
        var act = async () => await log.PublishAsync(Guid.NewGuid(), "clearpay.transfer.completed", "{}", Guid.NewGuid());
        await act.Should().NotThrowAsync();
    }
}
