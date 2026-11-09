using ClearPay.Application.Ports;
using Hangfire;

namespace ClearPay.Infrastructure.Jobs;

/// <summary>Every minute: challenges past their deadline become TimedOut (with an outbox event).</summary>
public sealed class ThreeDsExpiryHangfireJob
{
    private readonly IThreeDsService _service;

    public ThreeDsExpiryHangfireJob(IThreeDsService service)
    {
        _service = service;
    }

    [AutomaticRetry(Attempts = 0)]
    [DisableConcurrentExecution(60)]
    public Task Run() => _service.ExpireStaleAsync(CancellationToken.None);
}
