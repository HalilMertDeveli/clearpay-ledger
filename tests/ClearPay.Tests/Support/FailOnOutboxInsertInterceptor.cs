using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ClearPay.Tests.Support;

/// <summary>Thrown by the test interceptor to simulate a database failure mid-transaction.</summary>
public sealed class SimulatedDatabaseFailureException : Exception
{
    public SimulatedDatabaseFailureException()
        : base("Simulated database failure after the outbox insert executed.")
    {
    }
}

/// <summary>
/// Lets the INSERT into OutboxMessage reach the database inside the open transaction,
/// then throws. Every row staged before it (ledger pair, transfer, idempotency, audit)
/// has also been sent to the server, so only a real ROLLBACK can make them disappear.
/// </summary>
public sealed class FailOnOutboxInsertInterceptor : DbCommandInterceptor
{
    public bool Enabled { get; set; } = true;

    public int Failures { get; private set; }

    public override DbDataReader ReaderExecuted(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result)
    {
        ThrowIfOutboxInsert(command, result);
        return result;
    }

    public override ValueTask<DbDataReader> ReaderExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        DbDataReader result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfOutboxInsert(command, result);
        return ValueTask.FromResult(result);
    }

    public override int NonQueryExecuted(DbCommand command, CommandExecutedEventData eventData, int result)
    {
        ThrowIfOutboxInsert(command, null);
        return result;
    }

    public override ValueTask<int> NonQueryExecutedAsync(
        DbCommand command,
        CommandExecutedEventData eventData,
        int result,
        CancellationToken cancellationToken = default)
    {
        ThrowIfOutboxInsert(command, null);
        return ValueTask.FromResult(result);
    }

    private void ThrowIfOutboxInsert(DbCommand command, DbDataReader? reader)
    {
        if (!Enabled)
            return;
        var text = command.CommandText;
        if (!text.Contains("INSERT", StringComparison.OrdinalIgnoreCase)
            || !text.Contains("OutboxMessage", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        Failures++;
        reader?.Dispose();
        throw new SimulatedDatabaseFailureException();
    }
}
