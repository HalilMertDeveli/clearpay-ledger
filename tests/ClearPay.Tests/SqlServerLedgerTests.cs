using System.Net;
using ClearPay.Domain.Ledger;
using ClearPay.Infrastructure.Persistence;
using ClearPay.Tests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClearPay.Tests;

/// <summary>
/// Same money rules, but on the production database engine: SQL Server with the real
/// EF migrations, Serializable transactions, the unique Idempotency-Key index and the
/// LedgerEntry CHECK constraint. Concurrency is only meaningful here (SQLite serialises writers).
/// </summary>
public sealed class SqlServerLedgerTests : IClassFixture<SqlServerFixture>
{
    private readonly SqlServerFixture _sql;

    public SqlServerLedgerTests(SqlServerFixture sql)
    {
        _sql = sql;
    }

    [SqlServerFact]
    public async Task Concurrent_requests_with_the_same_idempotency_key_post_exactly_one_transfer()
    {
        var client = _sql.Factory.CreateClient();
        var sender = await ApiSession.RegisterAsync(client, "racekey");
        var receiver = await ApiSession.RegisterAsync(client, "racekeyto");
        await ApiSession.TopUpAsync(client, sender, 100m);

        var key = ApiSession.NewKey("race");
        var responses = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            ApiSession.TransferAsync(_sql.Factory.CreateClient(), sender.Token, key, receiver.Email, 10m)));
        var codes = responses.Select(r => (int)r.StatusCode).ToList();

        codes.Count(c => c == 201).Should().Be(1, "exactly one request may win; codes were {0}", string.Join(",", codes));
        // Losers either see the committed key (409) or lose the Serializable race as a
        // deadlock victim (503 "nothing was charged, retry"). Neither may post money.
        codes.Where(c => c != 201).Should().OnlyContain(c => c == 409 || c == 503);

        (await ApiSession.BalanceAsync(client, sender)).Should().Be(90m);
        (await ApiSession.BalanceAsync(client, receiver)).Should().Be(10m);
        await using var db = _sql.CreateContext();
        (await db.IdempotencyRecords.CountAsync(r => r.Key == key)).Should().Be(1);
        var senderWallet = await db.Wallets.SingleAsync(w => w.UserId == sender.Id);
        (await db.Transfers.CountAsync(t => t.FromWalletId == senderWallet.Id)).Should().Be(1);
        await AssertLedgerBalancedAsync(db);

        // A retry after the race is a clean 409, not a second debit.
        (await ApiSession.TransferAsync(client, sender.Token, key, receiver.Email, 10m))
            .StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await ApiSession.BalanceAsync(client, sender)).Should().Be(90m);
    }

    [SqlServerFact]
    public async Task Concurrent_transfers_from_one_wallet_never_overdraw_it()
    {
        var client = _sql.Factory.CreateClient();
        var sender = await ApiSession.RegisterAsync(client, "drain");
        var receiver = await ApiSession.RegisterAsync(client, "drainto");
        await ApiSession.TopUpAsync(client, sender, 100m);

        var responses = await Task.WhenAll(Enumerable.Range(0, 10).Select(i =>
            ApiSession.TransferAsync(
                _sql.Factory.CreateClient(), sender.Token, ApiSession.NewKey("drain" + i), receiver.Email, 30m)));
        var codes = responses.Select(r => (int)r.StatusCode).ToList();
        var succeeded = codes.Count(c => c == 201);

        succeeded.Should().BeInRange(1, 3, "100 can fund at most three transfers of 30; codes were {0}", string.Join(",", codes));
        codes.Where(c => c != 201).Should().OnlyContain(c => c == 422 || c == 503);

        var senderBalance = await ApiSession.BalanceAsync(client, sender);
        senderBalance.Should().Be(100m - (30m * succeeded));
        senderBalance.Should().BeGreaterThanOrEqualTo(0m);
        (await ApiSession.BalanceAsync(client, receiver)).Should().Be(30m * succeeded);

        await using var db = _sql.CreateContext();
        var senderWallet = await db.Wallets.SingleAsync(w => w.UserId == sender.Id);
        (await db.Transfers.CountAsync(t => t.FromWalletId == senderWallet.Id)).Should().Be(succeeded);
        await AssertLedgerBalancedAsync(db);
    }

    [SqlServerFact]
    public async Task Oversized_key_or_description_is_400_not_a_database_error()
    {
        var client = _sql.Factory.CreateClient();
        var sender = await ApiSession.RegisterAsync(client, "limits");
        var receiver = await ApiSession.RegisterAsync(client, "limitsto");
        await ApiSession.TopUpAsync(client, sender, 50m);

        var longKey = new string('k', LedgerSchema.IdempotencyKeyMaxLength + 1);
        (await ApiSession.TransferAsync(client, sender.Token, longKey, receiver.Email, 5m))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ApiSession.TransferAsync(client, sender.Token, ApiSession.NewKey("desc"), receiver.Email, 5m, new string('d', 501)))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);

        (await ApiSession.BalanceAsync(client, sender)).Should().Be(50m);
        await using var db = _sql.CreateContext();
        var senderWallet = await db.Wallets.SingleAsync(w => w.UserId == sender.Id);
        (await db.Transfers.CountAsync(t => t.FromWalletId == senderWallet.Id)).Should().Be(0);
    }

    [SqlServerFact]
    public Task Failure_mid_transaction_rolls_back_everything_on_sql_server() =>
        TransferRollbackScenario.RunAsync(interceptor => _sql.CreateContext(interceptor));

    [SqlServerFact]
    public async Task Database_rejects_a_zero_amount_ledger_row()
    {
        await using var db = _sql.CreateContext();
        var wallet = new Wallet { Id = Guid.NewGuid(), UserId = "zero-" + Guid.NewGuid().ToString("N"), CreatedAt = DateTimeOffset.UtcNow };
        db.Wallets.Add(wallet);
        await db.SaveChangesAsync();

        db.LedgerEntries.Add(new LedgerEntry
        {
            Id = Guid.NewGuid(),
            WalletId = wallet.Id,
            Amount = 0m,
            PairId = Guid.NewGuid(),
            CorrelationId = Guid.NewGuid(),
            Kind = LedgerEntryKind.Transfer,
            CreatedAt = DateTimeOffset.UtcNow
        });
        var act = () => db.SaveChangesAsync();

        (await act.Should().ThrowAsync<DbUpdateException>())
            .Which.InnerException!.Message.Should().Contain("CK_LedgerEntry_Amount_NotZero");
    }

    private static async Task AssertLedgerBalancedAsync(ClearPayDbContext db)
    {
        var all = await db.LedgerEntries.AsNoTracking().ToListAsync();
        all.Sum(e => e.Amount).Should().Be(0m);
        foreach (var pair in all.GroupBy(e => e.PairId))
        {
            pair.Should().HaveCount(2);
            pair.Sum(e => e.Amount).Should().Be(0m);
        }
    }
}
