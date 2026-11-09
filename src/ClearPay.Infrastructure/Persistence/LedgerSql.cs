using System.Data;
using System.Data.Common;
using ClearPay.Application.Reconciliation;
using ClearPay.Application.Wallets;
using ClearPay.Domain.Ledger;
using ClearPay.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace ClearPay.Infrastructure.Persistence;

/// <summary>
/// The hot read paths. On SQL Server they call the stored procedures created by the
/// <c>AddLedgerProceduresAndCoveringIndexes</c> migration (see <c>docs/sql</c>); on SQLite (tests) the
/// same answers come from LINQ. Calls join the caller's current transaction, so the overdraft check
/// keeps its Serializable range locks.
/// </summary>
public static class LedgerSql
{
    public const string BalanceProcedure = "dbo.usp_LedgerWalletBalance";
    public const string SummaryProcedure = "dbo.usp_LedgerWalletSummary";
    public const string SettlementProcedure = "dbo.usp_PaymentSettlementCandidates";

    /// <summary>Balance = SUM(Amount) of the wallet's entries.</summary>
    public static async Task<decimal> WalletNetAsync(ClearPayDbContext db, Guid walletId, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer())
        {
            var amounts = await db.LedgerEntries.Where(e => e.WalletId == walletId).Select(e => e.Amount)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return amounts.Sum();
        }

        await using var command = await CommandAsync(db, BalanceProcedure, cancellationToken).ConfigureAwait(false);
        AddParameter(command, "@WalletId", walletId, DbType.Guid);
        var value = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return value is null or DBNull ? 0m : Convert.ToDecimal(value, System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>Balance, this month's in/out and the last five movements in one round trip.</summary>
    public static async Task<(decimal Balance, decimal MonthIncoming, decimal MonthOutgoing, IReadOnlyList<WalletMovement> Last)>
        WalletSummaryAsync(ClearPayDbContext db, Guid walletId, DateTimeOffset monthStart, CancellationToken cancellationToken)
    {
        if (!db.Database.IsSqlServer())
        {
            var entries = await db.LedgerEntries.AsNoTracking().Where(e => e.WalletId == walletId)
                .Select(e => new { e.Amount, e.CreatedAt, e.Kind, e.CorrelationId })
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            var inMonth = entries.Where(e => e.CreatedAt >= monthStart).ToList();
            return (
                entries.Sum(e => e.Amount),
                inMonth.Where(e => e.Amount > 0m).Sum(e => e.Amount),
                decimal.Negate(inMonth.Where(e => e.Amount < 0m).Sum(e => e.Amount)),
                entries.OrderByDescending(e => e.CreatedAt).Take(5)
                    .Select(e => new WalletMovement(e.CreatedAt, e.Kind.ToString(), e.Amount, e.CorrelationId)).ToList());
        }

        await using var command = await CommandAsync(db, SummaryProcedure, cancellationToken).ConfigureAwait(false);
        AddParameter(command, "@WalletId", walletId, DbType.Guid);
        AddParameter(command, "@MonthStart", monthStart, DbType.DateTimeOffset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        decimal balance = 0m, incoming = 0m, outgoing = 0m;
        if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            balance = reader.GetDecimal(0);
            incoming = reader.GetDecimal(1);
            outgoing = reader.GetDecimal(2);
        }

        var last = new List<WalletMovement>();
        if (await reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                last.Add(new WalletMovement(
                    reader.GetFieldValue<DateTimeOffset>(0),
                    ((LedgerEntryKind)reader.GetInt32(1)).ToString(),
                    reader.GetDecimal(2),
                    reader.GetGuid(3)));
            }
        }

        return (balance, incoming, outgoing, last);
    }

    /// <summary>Captures and refunds sent to a PSP on one UTC business date that did not fail.</summary>
    public static async Task<IReadOnlyList<LedgerSideRecord>> SettlementCandidatesAsync(
        ClearPayDbContext db, DateOnly businessDate, CancellationToken cancellationToken)
    {
        var from = new DateTimeOffset(businessDate.ToDateTime(TimeOnly.MinValue), TimeSpan.Zero);
        var to = from.AddDays(1);
        if (!db.Database.IsSqlServer())
        {
            var ops = await db.PaymentOperations.AsNoTracking()
                .Where(o => (o.Type == PaymentOperationType.Capture || o.Type == PaymentOperationType.Refund)
                            && o.Status != PaymentOperationStatus.Failed)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            return ops.Where(o => o.CreatedAt >= from && o.CreatedAt < to)
                .Select(o => new LedgerSideRecord(o.Id, o.Provider, o.Type.ToString(), o.Status.ToString(),
                    o.ProviderReference, o.CorrelationId, o.Amount))
                .ToList();
        }

        await using var command = await CommandAsync(db, SettlementProcedure, cancellationToken).ConfigureAwait(false);
        AddParameter(command, "@From", from, DbType.DateTimeOffset);
        AddParameter(command, "@To", to, DbType.DateTimeOffset);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<LedgerSideRecord>();
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new LedgerSideRecord(
                reader.GetGuid(0),
                reader.GetString(1),
                ((PaymentOperationType)reader.GetInt32(2)).ToString(),
                ((PaymentOperationStatus)reader.GetInt32(3)).ToString(),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetGuid(5),
                reader.GetDecimal(6)));
        }

        return result;
    }

    private static async Task<DbCommand> CommandAsync(ClearPayDbContext db, string procedure, CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
            await db.Database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        var command = connection.CreateCommand();
        command.CommandText = procedure;
        command.CommandType = CommandType.StoredProcedure;
        command.Transaction = db.Database.CurrentTransaction?.GetDbTransaction();
        var timeout = db.Database.GetCommandTimeout();
        if (timeout is not null)
            command.CommandTimeout = timeout.Value;
        return command;
    }

    private static void AddParameter(DbCommand command, string name, object value, DbType type)
    {
        var p = command.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        p.DbType = type;
        command.Parameters.Add(p);
    }
}
