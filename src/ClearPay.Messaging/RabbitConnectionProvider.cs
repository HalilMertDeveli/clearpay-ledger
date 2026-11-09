using Microsoft.Extensions.Logging;
using RabbitMQ.Client;

namespace ClearPay.Messaging;

/// <summary>
/// One long-lived connection with automatic recovery. Connects lazily, so the app starts even when the
/// broker is down and picks it up later (at most one connect attempt every 10 seconds).
/// </summary>
public sealed class RabbitConnectionProvider : IDisposable
{
    private static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(10);
    private readonly RabbitOptions _options;
    private readonly ILogger<RabbitConnectionProvider> _logger;
    private readonly object _gate = new();
    private IConnection? _connection;
    private DateTimeOffset _nextAttempt = DateTimeOffset.MinValue;

    public RabbitConnectionProvider(RabbitOptions options, ILogger<RabbitConnectionProvider> logger)
    {
        _options = options;
        _logger = logger;
    }

    /// <summary>up, down, or off (no connection string).</summary>
    public string Status => !_options.Enabled ? "off" : TryGet() is { IsOpen: true } ? "up" : "down";

    public IConnection? TryGet()
    {
        if (!_options.Enabled)
            return null;
        lock (_gate)
        {
            if (_connection is { IsOpen: true })
                return _connection;
            if (DateTimeOffset.UtcNow < _nextAttempt)
                return null;
            try
            {
                _connection?.Dispose();
                var factory = new ConnectionFactory
                {
                    Uri = new Uri(_options.Uri!),
                    RequestedConnectionTimeout = TimeSpan.FromSeconds(2),
                    AutomaticRecoveryEnabled = true,
                    TopologyRecoveryEnabled = true,
                    DispatchConsumersAsync = true
                };
                _connection = factory.CreateConnection("clearpay");
                return _connection;
            }
            catch (Exception ex)
            {
                _connection = null;
                _nextAttempt = DateTimeOffset.UtcNow + RetryEvery;
                _logger.LogWarning(ex, "RabbitMQ not reachable; next attempt in {Seconds}s", RetryEvery.TotalSeconds);
                return null;
            }
        }
    }

    public IConnection Get() =>
        TryGet() ?? throw new InvalidOperationException("RabbitMQ is configured but not reachable.");

    public void Dispose()
    {
        lock (_gate)
        {
            _connection?.Dispose();
            _connection = null;
        }
    }
}
