using Microsoft.Extensions.Configuration;

namespace ClearPay.Messaging;

/// <summary>Messaging settings (section <c>Messaging</c>); the broker URI is <c>ConnectionStrings:RabbitMq</c>.</summary>
public sealed class RabbitOptions
{
    public string? Uri { get; init; }

    /// <summary>Topic exchange every outbox event is published to, routing key = event type.</summary>
    public string EventsExchange { get; init; } = "clearpay.events";

    /// <summary>Delay before attempt 2, 3, ... (TTL retry queues). Default 1 s, 5 s, 25 s.</summary>
    public IReadOnlyList<TimeSpan> RetryDelays { get; init; } =
        new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(25) };

    /// <summary>Total attempts including the first; after that the message goes to the dead-letter queue.</summary>
    public int MaxAttempts { get; init; } = 4;

    public ushort Prefetch { get; init; } = 10;

    public bool ConsumersEnabled { get; init; } = true;

    /// <summary>Prepended to queue names (tests and side-by-side environments on one broker).</summary>
    public string QueuePrefix { get; init; } = string.Empty;

    public bool Enabled => !string.IsNullOrWhiteSpace(Uri);

    public static RabbitOptions From(IConfiguration configuration)
    {
        var delays = configuration.GetSection("Messaging:RetryDelaysMs").Get<int[]>();
        return new RabbitOptions
        {
            Uri = configuration.GetConnectionString("RabbitMq"),
            EventsExchange = configuration["Messaging:EventsExchange"] ?? "clearpay.events",
            RetryDelays = delays is { Length: > 0 }
                ? delays.Select(ms => TimeSpan.FromMilliseconds(ms)).ToArray()
                : new[] { TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(25) },
            MaxAttempts = configuration.GetValue("Messaging:MaxAttempts", 4),
            Prefetch = configuration.GetValue<ushort>("Messaging:Prefetch", 10),
            ConsumersEnabled = configuration.GetValue("Messaging:ConsumersEnabled", true),
            QueuePrefix = configuration["Messaging:QueuePrefix"] ?? string.Empty
        };
    }
}
