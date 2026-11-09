using ClearPay.Application.Ports;
using ClearPay.Application.Wallets;
using ClearPay.Domain.Ledger;
using Microsoft.EntityFrameworkCore;

namespace ClearPay.Infrastructure.Persistence;

/// <summary>
/// TASK-05: özet from ledger net. No <c>UPDATE Balance</c>. PageModels do not call this type.
/// SQL unreachable → empty zeros (Identity-only site still loads).
/// </summary>
public sealed class SqlWalletReader : IWalletReader
{
    private readonly ClearPayDbContext _db;
    private readonly IClock _clock;

    public SqlWalletReader(ClearPayDbContext db, IClock clock)
    {
        _db = db;
        _clock = clock;
    }

    public async Task<WalletSummary?> GetByUserIdAsync(string userId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(userId))
            return Empty(userId ?? string.Empty);

        if (!await TryConnectAsync(cancellationToken).ConfigureAwait(false))
            return Empty(userId);

        var wallet = await _db.Wallets
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.UserId == userId, cancellationToken)
            .ConfigureAwait(false);

        if (wallet is null)
            wallet = await EnsureWalletAsync(userId, cancellationToken).ConfigureAwait(false);

        var now = _clock.UtcNow;
        var monthStart = new DateTimeOffset(now.Year, now.Month, 1, 0, 0, 0, TimeSpan.Zero);
        var summary = await LedgerSql.WalletSummaryAsync(_db, wallet.Id, monthStart, cancellationToken)
            .ConfigureAwait(false);

        return new WalletSummary(
            WalletId: wallet.Id,
            UserId: userId,
            Balance: summary.Balance,
            MonthOutgoing: summary.MonthOutgoing,
            MonthIncoming: summary.MonthIncoming,
            IsFrozen: wallet.IsFrozen,
            LastMovements: summary.Last);
    }

    private async Task<bool> TryConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            return await _db.Database.CanConnectAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception)
        {
            return false;
        }
    }

    private async Task<Wallet> EnsureWalletAsync(string userId, CancellationToken cancellationToken)
    {
        var wallet = new Wallet
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            IsFrozen = false,
            CreatedAt = _clock.UtcNow
        };

        _db.Wallets.Add(wallet);
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return wallet;
        }
        catch (DbUpdateException)
        {
            _db.ChangeTracker.Clear();
            var existing = await _db.Wallets
                .AsNoTracking()
                .SingleAsync(w => w.UserId == userId, cancellationToken)
                .ConfigureAwait(false);
            return existing;
        }
    }

    private static WalletSummary Empty(string userId) => new(
        WalletId: Guid.Empty,
        UserId: userId,
        Balance: 0m,
        MonthOutgoing: 0m,
        MonthIncoming: 0m,
        IsFrozen: false,
        LastMovements: Array.Empty<WalletMovement>());
}
