using System.Data;
using System.Data.Common;
using ClearPay.Application.Wallets;
using ClearPay.Domain.Ledger;
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
