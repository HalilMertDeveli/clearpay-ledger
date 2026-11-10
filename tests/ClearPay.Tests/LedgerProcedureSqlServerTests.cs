using ClearPay.Domain.Ledger;
using ClearPay.Infrastructure.Persistence;
using ClearPay.Tests.Support;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;

namespace ClearPay.Tests;

/// <summary>The stored procedures give the same answers as the LINQ they replace, and use the covering indexes.</summary>
public sealed class LedgerProcedureSqlServerTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _fixture;

    public LedgerProcedureSqlServerTests(SqlServerFixture fixture)
    {
        _fixture = fixture;
    }

    [SqlServerFact]
    public async Task Balance_and_summary_procedures_match_the_entries()
    {
        await using var db = _fixture.CreateContext();
        var wallet = new Wallet { Id = Guid.NewGuid(), UserId = "proc-" + Guid.NewGuid().ToString("N")[..8], CreatedAt = DateTimeOffset.UtcNow };
        var other = new Wallet { Id = Guid.NewGuid(), UserId = "proc-o-" + Guid.NewGuid().ToString("N")[..8], CreatedAt = DateTimeOffset.UtcNow };
        db.Wallets.AddRange(wallet, other);
        var monthStart = new DateTimeOffset(2026, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var rows = new[] { (100m, -40), (35.5m, -1), (-20m, 2), (12.25m, 5), (-7.75m, 6), (3m, 7) };
        foreach (var (amount, dayOffset) in rows)
        {
            var at = monthStart.AddDays(dayOffset);
            var (debit, credit) = amount > 0
                ? LedgerPair.Create(other.Id, wallet.Id, amount, Guid.NewGuid(), LedgerEntryKind.Transfer, at: at)
                : LedgerPair.Create(wallet.Id, other.Id, -amount, Guid.NewGuid(), LedgerEntryKind.Transfer, at: at);
            db.LedgerEntries.AddRange(debit, credit);
        }

        await db.SaveChangesAsync();

        (await LedgerSql.WalletNetAsync(db, wallet.Id, CancellationToken.None)).Should().Be(123m);
        (await LedgerSql.WalletNetAsync(db, Guid.NewGuid(), CancellationToken.None)).Should().Be(0m);

        var summary = await LedgerSql.WalletSummaryAsync(db, wallet.Id, monthStart, CancellationToken.None);
        summary.Balance.Should().Be(123m);
        summary.MonthIncoming.Should().Be(15.25m);
        summary.MonthOutgoing.Should().Be(27.75m);
        summary.Last.Select(m => m.Amount).Should().Equal(3m, -7.75m, 12.25m, -20m, 35.5m);
    }

    [SqlServerFact]
    public async Task Balance_procedure_runs_inside_the_callers_transaction()
    {
        await using var db = _fixture.CreateContext();
        var wallet = new Wallet { Id = Guid.NewGuid(), UserId = "tx-" + Guid.NewGuid().ToString("N")[..8], CreatedAt = DateTimeOffset.UtcNow };
        var other = new Wallet { Id = Guid.NewGuid(), UserId = "tx-o-" + Guid.NewGuid().ToString("N")[..8], CreatedAt = DateTimeOffset.UtcNow };
        db.Wallets.AddRange(wallet, other);
        await db.SaveChangesAsync();

        await using var tx = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.Serializable);
        var (debit, credit) = LedgerPair.Create(other.Id, wallet.Id, 50m, Guid.NewGuid(), LedgerEntryKind.TopUp);
        db.LedgerEntries.AddRange(debit, credit);
        await db.SaveChangesAsync();

        (await LedgerSql.WalletNetAsync(db, wallet.Id, CancellationToken.None)).Should().Be(50m, "uncommitted rows of the same transaction are visible");
        await tx.RollbackAsync();
        (await LedgerSql.WalletNetAsync(db, wallet.Id, CancellationToken.None)).Should().Be(0m);
    }

    [SqlServerFact]
    public async Task Procedures_and_covering_indexes_exist_after_migration()
    {
        await using var connection = new SqlConnection(_fixture.ConnectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
              (SELECT COUNT(*) FROM sys.procedures WHERE name IN ('usp_LedgerWalletBalance','usp_LedgerWalletSummary')),
              (SELECT COUNT(*) FROM sys.index_columns ic JOIN sys.indexes i ON i.object_id = ic.object_id AND i.index_id = ic.index_id
                 WHERE i.name = 'IX_LedgerEntry_WalletId_CreatedAt' AND ic.is_included_column = 1),
              (SELECT COUNT(*) FROM sys.indexes WHERE name = 'IX_PaymentOperation_CreatedAt_Type') + (SELECT COUNT(*) FROM sys.procedures WHERE name = 'usp_PaymentSettlementCandidates')
            """;
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        reader.GetInt32(0).Should().Be(2);
        reader.GetInt32(1).Should().Be(3, "Amount, Kind, CorrelationId are included");
        reader.GetInt32(2).Should().Be(0, "reconciliation and its index moved to the Operations service");
    }
}
