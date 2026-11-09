using ClearPay.Application.Ports;
using Hangfire;

namespace ClearPay.Infrastructure.Jobs;

/// <summary>Nightly (02:30 UTC): reconcile yesterday's captures and refunds against every PSP settlement file.</summary>
public sealed class ReconciliationHangfireJob
{
    private readonly IReconciliationService _service;
    private readonly IClock _clock;

    public ReconciliationHangfireJob(IReconciliationService service, IClock clock)
    {
        _service = service;
        _clock = clock;
    }

    /// <summary>Hangfire retries a failed run (file not there yet, DB blip); a completed date is skipped.</summary>
    [AutomaticRetry(Attempts = 3)]
    [DisableConcurrentExecution(600)]
    public Task Run() =>
        _service.RunAsync(DateOnly.FromDateTime(_clock.UtcNow.UtcDateTime).AddDays(-1), force: false, CancellationToken.None);
}
