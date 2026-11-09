using ClearPay.Application.Ports;
using ClearPay.Application.Reconciliation;
using ClearPay.Domain.Payments;
using ClearPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace ClearPay.Infrastructure.Payments.Psp;

/// <summary>
/// Plays the PSPs' end-of-day process: writes <c>{provider}_{yyyyMMdd}.csv</c> from the simulators'
/// own journal (<c>sim.PspTransaction</c>) into <c>Reconciliation:SettlementDirectory</c>, then hands the
/// file content to the reconciliation job. Only approved captures and refunds are settled.
/// Documented faults so reconciliation has something to find:
/// cents .77 are left out of the file, cents .78 are settled one kuruş higher.
/// </summary>
public sealed class SimSettlementFileSource : ISettlementFileSource
{
    private readonly IServiceScopeFactory _scopes;
    private readonly string _directory;

    public SimSettlementFileSource(IServiceScopeFactory scopes, IConfiguration configuration)
    {
        _scopes = scopes;
        _directory = configuration["Reconciliation:SettlementDirectory"]
            ?? Path.Combine(AppContext.BaseDirectory, "App_Data", "settlements");
    }

    public IReadOnlyCollection<string> Providers { get; } =
        new[] { SimBankRestPsp.ProviderName, SimBankSoapPsp.ProviderName, SimPayPsp.ProviderName };

    public async Task<string?> FetchAsync(string provider, DateOnly businessDate, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClearPayDbContext>();
        var rows = await db.SimPspTransactions.AsNoTracking()
            .Where(t => t.Provider == provider && t.Approved
                        && (t.Type == PaymentOperationType.Capture || t.Type == PaymentOperationType.Refund))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var lines = rows
            .Where(t => DateOnly.FromDateTime(t.CreatedAt.UtcDateTime) == businessDate)
            .Where(t => SimulatedPspCore.Cents(t.Amount) != 77)
            .OrderBy(t => t.CreatedAt)
            .Select(t => new SettlementLine(
                provider,
                businessDate,
                t.Type.ToString(),
                t.Reference,
                t.CorrelationId,
                SimulatedPspCore.Cents(t.Amount) == 78 ? t.Amount + 0.01m : t.Amount,
                "TRY"))
            .ToList();

        var content = SettlementCsv.Write(lines);
        Directory.CreateDirectory(_directory);
        var path = Path.Combine(_directory, $"{provider}_{businessDate:yyyyMMdd}.csv");
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);
        return await File.ReadAllTextAsync(path, cancellationToken).ConfigureAwait(false);
    }
}
