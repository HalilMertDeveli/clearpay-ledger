using ClearPay.Messaging;
using ClearPay.Application.Ports;
using ClearPay.Infrastructure.Messaging;
using ClearPay.Infrastructure.Messaging.Handlers;
using ClearPay.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using RabbitMQ.Client;

namespace ClearPay.Tests.Support;

/// <summary>Runs only when <c>CLEARPAY_TEST_RABBITMQ</c> holds an AMQP URI (CI: rabbitmq service container).</summary>
public sealed class RabbitFactAttribute : FactAttribute
{
    public const string EnvironmentVariable = "CLEARPAY_TEST_RABBITMQ";

    public RabbitFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EnvironmentVariable)))
            Skip = $"RabbitMQ integration test: set {EnvironmentVariable} to run it.";
    }
}

/// <summary>
/// Real broker, real consumer service, SQLite ledger. Exchange and queues get a unique prefix and are deleted
/// afterwards, so tests never see each other's messages. Retry delays are shortened to 200/400 ms.
/// </summary>
internal sealed class RabbitTestHost : IAsyncDisposable
{
    private readonly SqliteConnection _sqlite = new("Data Source=:memory:");
    private readonly ServiceProvider _provider;
    private readonly string _prefix = "test-" + Guid.NewGuid().ToString("N")[..8] + ".";

    public RabbitTestHost(Action<IServiceCollection, MessageHandlerRegistry> registerHandlers, int maxAttempts = 3)
    {
        _sqlite.Open();
        Options = new RabbitOptions
        {
            Uri = Environment.GetEnvironmentVariable(RabbitFactAttribute.EnvironmentVariable),
            EventsExchange = _prefix + "events",
            QueuePrefix = _prefix,
            RetryDelays = new[] { TimeSpan.FromMilliseconds(200), TimeSpan.FromMilliseconds(400) },
            MaxAttempts = maxAttempts
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddProvider(NullLoggerProvider.Instance));
        services.AddDbContext<ClearPayDbContext>(o => o.UseSqlite(_sqlite));
        services.AddSingleton<IClock>(new TestClock { UtcNow = DateTimeOffset.UtcNow });
        services.AddSingleton<IWalletSummaryCache, NullWalletCache>();
        services.AddSingleton<IConfiguration>(new ConfigurationBuilder().AddInMemoryCollection().Build());
        services.AddSingleton(Options);
        services.AddSingleton<RabbitConnectionProvider>();
        services.AddSingleton<RabbitEventPublisher>();
        services.AddSingleton<RabbitOutboxPublisher>();
        services.AddScoped(typeof(InboxProcessor<>));
        services.AddScoped<CashbackOnCaptureHandler>();
        var registry = new MessageHandlerRegistry();
        registerHandlers(services, registry);
        services.AddSingleton(registry);
        services.AddSingleton<RabbitConsumerService>();
        _provider = services.BuildServiceProvider();

        using var scope = _provider.CreateScope();
        scope.ServiceProvider.GetRequiredService<ClearPayDbContext>().Database.EnsureCreated();
    }

    public RabbitOptions Options { get; }

    public string Queue(string consumer) => _prefix + consumer;

    public RabbitOutboxPublisher Publisher => _provider.GetRequiredService<RabbitOutboxPublisher>();

    public IConnection Connection => _provider.GetRequiredService<RabbitConnectionProvider>().Get();

    public RabbitConsumerService Consumers => _provider.GetRequiredService<RabbitConsumerService>();

    public T Resolve<T>()
        where T : notnull => _provider.GetRequiredService<T>();

    public ClearPayDbContext NewContext() => _provider.CreateScope().ServiceProvider.GetRequiredService<ClearPayDbContext>();

    public async Task StartConsumersAsync()
    {
        await Consumers.StartAsync(CancellationToken.None);
        await Consumers.Started.WaitAsync(TimeSpan.FromSeconds(10));
    }

    /// <summary>Declares the consumer's queues without consuming (to stage messages before a consumer starts).</summary>
    public void DeclareQueues(string consumer, params string[] keys)
    {
        using var channel = Connection.CreateModel();
        RabbitTopology.DeclareConsumer(channel, Options, Queue(consumer), keys);
    }

    public uint Count(string queue)
    {
        using var channel = Connection.CreateModel();
        return channel.MessageCount(queue);
    }

    public BasicGetResult? Get(string queue, bool ack = true)
    {
        using var channel = Connection.CreateModel();
        return channel.BasicGet(queue, ack);
    }

    public static async Task WaitUntilAsync(Func<Task<bool>> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (await condition())
                return;
            await Task.Delay(50);
        }

        throw new TimeoutException("Condition not met in time.");
    }

    public async ValueTask DisposeAsync()
    {
        await Consumers.StopAsync(CancellationToken.None);
        Consumers.Dispose();
        try
        {
            using var channel = Connection.CreateModel();
            foreach (var consumer in new[] { CashbackOnCaptureHandler.ConsumerName })
            {
                var q = Queue(consumer);
                channel.QueueDelete(q);
                channel.QueueDelete(RabbitTopology.DeadLetterQueue(q));
                for (var i = 1; i <= Options.RetryDelays.Count; i++)
                    channel.QueueDelete(RabbitTopology.RetryQueue(q, i));
            }

            channel.ExchangeDelete(Options.EventsExchange);
        }
        catch (Exception)
        {
            // Best effort cleanup of a throw-away broker.
        }

        await _provider.DisposeAsync();
        await _sqlite.DisposeAsync();
    }
}
