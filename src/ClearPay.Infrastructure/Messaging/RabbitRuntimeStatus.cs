namespace ClearPay.Infrastructure.Messaging;

/// <summary>Live broker state for /api/health.</summary>
public sealed class RabbitRuntimeStatus
{
    private readonly RabbitConnectionProvider _connections;

    public RabbitRuntimeStatus(RabbitConnectionProvider connections) => _connections = connections;

    /// <summary>up, down, or off (no connection string).</summary>
    public string Value => _connections.Status;
}
