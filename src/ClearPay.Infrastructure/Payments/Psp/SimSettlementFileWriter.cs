using ClearPay.Application.Ports;
using ClearPay.Application.Reconciliation;
using ClearPay.Domain.Payments;
using ClearPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ClearPay.Infrastructure.Payments.Psp;

/// <summary>
/// Plays the PSPs' end-of-day process: writes <c>{provider}_{yyyyMMdd}.csv</c> from the simulators' own
/// journal (<c>sim.PspTransaction</c>) into <c>Reconciliation:SettlementDirectory</c> - the drop folder the
/// Operations service reads (an SFTP or bucket drop in real life). Only approved captures and refunds are settled.
/// Documented faults so reconciliation has something to find:
/// cents .77 are left out of the file, cents .78 are settled one kuruş higher.
/// </summary>
public sealed class SimSettlementFileWriter : IPspSettlementFileWriter
{
    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<SimSettlementFileWriter> _logger;
    private readonly string _directory;

    public SimSettlementFileWriter(IServiceScopeFactory scopes, IConfiguration configuration, ILogger<SimSettlementFileWriter> logger)
    {
        _scopes = scopes;
        _logger = logger;
        _directory = SettlementDirectory(configuration);
    }

    public static readonly IReadOnlyList<string> Providers =
        new[] { SimBankRestPsp.ProviderName, SimBankSoapPsp.ProviderName, SimPayPsp.ProviderName };

    /// <summary>Shared by the PSP simulators (writer) and the Operations service (reader).</summary>
    public static string SettlementDirectory(IConfiguration configuration) =>
        string.IsNullOrWhiteSpace(configuration["Reconciliation:SettlementDirectory"])
            ? Path.Combine(Path.GetTempPath(), "clearpay-settlements")
            : configuration["Reconciliation:SettlementDirectory"]!;

    public async Task<IReadOnlyList<string>> WriteAsync(DateOnly businessDate, CancellationToken cancellationToken = default)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClearPayDbContext>();
        var rows = await db.SimPspTransactions.AsNoTracking()
            .Where(t => t.Approved && (t.Type == PaymentOperationType.Capture || t.Type == PaymentOperationType.Refund))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        Directory.CreateDirectory(_directory);
        var written = new List<string>();
        foreach (var provider in Providers)
        {
            var lines = rows
                .Where(t => t.Provider == provider && DateOnly.FromDateTime(t.CreatedAt.UtcDateTime) == businessDate)
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

            var path = Path.Combine(_directory, $"{provider}_{businessDate:yyyyMMdd}.csv");
            var temp = path + ".part";
            await File.WriteAllTextAsync(temp, SettlementCsv.Write(lines), cancellationToken).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true); // readers never see a half-written file
            written.Add(path);
            _logger.LogInformation("PSP {Provider} settlement file for {BusinessDate}: {Lines} lines -> {Path}",
                provider, businessDate, lines.Count, path);
        }

        return written;
    }
}
