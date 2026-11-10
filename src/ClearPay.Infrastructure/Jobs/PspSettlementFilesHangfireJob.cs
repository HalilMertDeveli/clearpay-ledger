using ClearPay.Application.Ports;
using Hangfire;

namespace ClearPay.Infrastructure.Jobs;

/// <summary>01:00 UTC: the simulated PSPs drop yesterday's settlement files (the Operations service reconciles at 02:30).</summary>
public sealed class PspSettlementFilesHangfireJob
{
    private readonly IPspSettlementFileWriter _writer;
    private readonly IClock _clock;

    public PspSettlementFilesHangfireJob(IPspSettlementFileWriter writer, IClock clock)
    {
        _writer = writer;
        _clock = clock;
    }

    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(600)]
    public Task Run() =>
        _writer.WriteAsync(DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime).AddDays(-1), CancellationToken.None);
}
