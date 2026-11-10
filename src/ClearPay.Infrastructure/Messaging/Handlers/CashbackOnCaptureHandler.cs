using ClearPay.Messaging;
using System.Text.Json;
using ClearPay.Application.Ports;
using ClearPay.Domain.Ledger;
using ClearPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;

namespace ClearPay.Infrastructure.Messaging.Handlers;

/// <summary>
/// Demo promotion: on <c>clearpay.payment.captured</c> credit a cashback (default 1 %) from the
/// promotions wallet to the customer. It is a ledger posting driven by a message, which is exactly where
/// a redelivery must not post twice - the inbox row and the ledger pair commit together.
/// </summary>
public sealed class CashbackOnCaptureHandler : IMessageHandler
{
    public const string ConsumerName = "clearpay.cashback";

    /// <summary>Promotion budget purse; like the treasury it may go negative. Not a login user.</summary>
    public const string PromotionsUserId = "clearpay-promotions";

    private readonly InboxProcessor<ClearPayDbContext> _inbox;
    private readonly IClock _clock;
    private readonly IWalletSummaryCache _cache;
    private readonly decimal _percent;

    public CashbackOnCaptureHandler(
        InboxProcessor<ClearPayDbContext> inbox, IClock clock, IWalletSummaryCache cache, IConfiguration configuration)
    {
        _inbox = inbox;
        _clock = clock;
        _cache = cache;
        _percent = configuration.GetValue("Cashback:Percent", 1m);
    }

    public string Consumer => ConsumerName;

    public IReadOnlyCollection<string> RoutingKeys { get; } = new[] { "clearpay.payment.captured" };

    public static decimal CashbackFor(decimal amount, decimal percent) =>
        decimal.Round(amount * percent / 100m, 2, MidpointRounding.ToZero);

    public async Task<bool> HandleAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        string userId;
        decimal amount;
        Guid paymentId;
        try
        {
            var json = message.Json();
            userId = json.GetProperty("userId").GetString() ?? throw new FormatException("userId missing");
            amount = json.GetProperty("amount").GetDecimal();
            paymentId = json.GetProperty("paymentId").GetGuid();
        }
        catch (Exception ex) when (ex is JsonException or FormatException or KeyNotFoundException or InvalidOperationException)
        {
            throw new PoisonMessageException("Unreadable payment.captured payload", ex);
        }

        var cashback = CashbackFor(amount, _percent);
        var processed = await _inbox.ProcessAsync(Consumer, message, async (db, msg, ct) =>
        {
            if (cashback <= 0m)
                return;
            var customer = await WalletAsync(db, userId, ct).ConfigureAwait(false);
            var promotions = await WalletAsync(db, PromotionsUserId, ct).ConfigureAwait(false);
            var correlation = msg.CorrelationId != Guid.Empty ? msg.CorrelationId : msg.MessageId;
            var (debit, credit) = LedgerPair.Create(promotions.Id, customer.Id, cashback, correlation,
                LedgerEntryKind.Cashback, description: $"Cashback {_percent}% for card payment", at: _clock.UtcNow);
            db.LedgerEntries.AddRange(debit, credit);
            db.AuditLogs.Add(new AuditLog
            {
                Id = Guid.NewGuid(),
                ActorUserId = Consumer,
                Action = "cashback.credited",
                CorrelationId = correlation,
                Details = JsonSerializer.Serialize(new { paymentId, cashback, messageId = msg.MessageId }),
                CreatedAt = _clock.UtcNow
            });
        }, _clock.UtcNow, cancellationToken).ConfigureAwait(false);

        if (processed)
            await _cache.InvalidateAsync(userId, cancellationToken).ConfigureAwait(false);
        return processed;
    }

    private async Task<Wallet> WalletAsync(ClearPayDbContext db, string userId, CancellationToken ct)
    {
        var wallet = db.Wallets.Local.FirstOrDefault(w => w.UserId == userId)
            ?? await db.Wallets.FirstOrDefaultAsync(w => w.UserId == userId, ct).ConfigureAwait(false);
        if (wallet is not null)
            return wallet;
        wallet = new Wallet { Id = Guid.NewGuid(), UserId = userId, IsFrozen = false, CreatedAt = _clock.UtcNow };
        db.Wallets.Add(wallet);
        return wallet;
    }
}
