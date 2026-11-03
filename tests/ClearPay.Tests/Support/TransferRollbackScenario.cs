using ClearPay.Application.Transfers;
using ClearPay.Domain.Ledger;
using ClearPay.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ClearPay.Tests.Support;

/// <summary>
/// Shared scenario: a transfer whose transaction fails after its rows were sent to the
/// database must leave no trace (no ledger pair, transfer, idempotency key, audit or
/// outbox row), and the same Idempotency-Key must still work afterwards.
/// Run against SQLite (always) and SQL Server (when configured).
/// </summary>
internal static class TransferRollbackScenario
{
    public static async Task RunAsync(Func<IInterceptor?, ClearPayDbContext> newContext)
    {
        var clock = new TestClock();
        var users = new DictionaryUserDirectory();
        var suffix = Guid.NewGuid().ToString("N");
        var senderId = "rb-sender-" + suffix;
        var recipientId = "rb-recipient-" + suffix;
        var recipientEmail = $"rb.{suffix}@clearpay.test";
        users.Map($"rb.sender.{suffix}@clearpay.test", senderId);
        users.Map(recipientEmail, recipientId);

        var senderWallet = new Wallet { Id = Guid.NewGuid(), UserId = senderId, CreatedAt = clock.UtcNow };
        var recipientWallet = new Wallet { Id = Guid.NewGuid(), UserId = recipientId, CreatedAt = clock.UtcNow };
        var funder = new Wallet { Id = Guid.NewGuid(), UserId = "rb-funder-" + suffix, CreatedAt = clock.UtcNow };
        await using (var seed = newContext(null))
        {
            seed.Wallets.AddRange(senderWallet, recipientWallet, funder);
            var (debit, credit) = LedgerPair.Create(
                funder.Id, senderWallet.Id, 100m, Guid.NewGuid(), LedgerEntryKind.TopUp, at: clock.UtcNow);
            seed.LedgerEntries.AddRange(debit, credit);
            await seed.SaveChangesAsync();
        }

        var key = "rb-key-" + suffix;
        var command = new TransferCommand(senderId, recipientEmail, 30m, "rollback", key);
        var interceptor = new FailOnOutboxInsertInterceptor();

        await using (var failing = newContext(interceptor))
        {
            var executor = NewExecutor(failing, users, clock);
            var act = () => executor.ExecuteAsync(command);
            var thrown = await act.Should().ThrowAsync<Exception>();
            Chain(thrown.Which).Should().Contain(e => e is SimulatedDatabaseFailureException);
            interceptor.Failures.Should().Be(1);
        }

        await using (var verify = newContext(null))
        {
            (await verify.Transfers.CountAsync(t => t.FromWalletId == senderWallet.Id)).Should().Be(0);
            (await verify.LedgerEntries.CountAsync(e =>
                    e.Kind == LedgerEntryKind.Transfer
                    && (e.WalletId == senderWallet.Id || e.WalletId == recipientWallet.Id)))
                .Should().Be(0);
            (await verify.IdempotencyRecords.CountAsync(r => r.Key == key)).Should().Be(0, "the key reservation is part of the rolled back transaction");
            (await verify.AuditLogs.CountAsync(a => a.ActorUserId == senderId)).Should().Be(0);
            (await verify.OutboxMessages.CountAsync(m => m.Payload.Contains(senderId))).Should().Be(0);
            var rows = await verify.LedgerEntries.AsNoTracking()
                .Where(e => e.WalletId == senderWallet.Id || e.WalletId == recipientWallet.Id)
                .ToListAsync();
            LedgerPair.NetOf(rows, senderWallet.Id).Should().Be(100m);
            LedgerPair.NetOf(rows, recipientWallet.Id).Should().Be(0m);
        }

        await using (var retry = newContext(null))
        {
            var outcome = await NewExecutor(retry, users, clock).ExecuteAsync(command);
            outcome.Kind.Should().Be(TransferResultKind.Created, "a rolled back attempt must not burn the Idempotency-Key");
        }

        await using (var after = newContext(null))
        {
            var rows = await after.LedgerEntries.AsNoTracking()
                .Where(e => e.WalletId == senderWallet.Id || e.WalletId == recipientWallet.Id)
                .ToListAsync();
            LedgerPair.NetOf(rows, senderWallet.Id).Should().Be(70m);
            LedgerPair.NetOf(rows, recipientWallet.Id).Should().Be(30m);
            (await after.Transfers.CountAsync(t => t.FromWalletId == senderWallet.Id)).Should().Be(1);
            (await after.AuditLogs.CountAsync(a => a.ActorUserId == senderId)).Should().Be(1);
            (await after.OutboxMessages.CountAsync(m => m.Payload.Contains(senderId))).Should().Be(1);
        }
    }

    private static SqlTransferExecutor NewExecutor(ClearPayDbContext db, DictionaryUserDirectory users, TestClock clock) =>
        new(db, users, new SqlIdempotencyStore(db, clock), clock, new NullWalletCache(), new NullLiveNotifier());

    private static IEnumerable<Exception> Chain(Exception exception)
    {
        for (var e = exception; e is not null; e = e.InnerException)
            yield return e;
    }
}
