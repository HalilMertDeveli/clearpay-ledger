using IsolationLevel = System.Data.IsolationLevel;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ClearPay.Application.Banking;
using ClearPay.Application.Payments;
using ClearPay.Application.Ports;
using ClearPay.Application.Transfers;
using ClearPay.Domain.Ledger;
using ClearPay.Domain.Payments;
using ClearPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClearPay.Infrastructure.Payments;

/// <summary>
/// Card payments behind <see cref="IPaymentServiceProvider"/> adapters.
/// Pattern for every operation: reserve in SQL (idempotency key, operation row, money rules) ->
/// call the PSP outside any transaction with a deterministic provider key -> record the answer in SQL.
/// A retry therefore never executes twice at the PSP and never posts the ledger twice.
/// <list type="bullet">
/// <item>Capture credits the wallet (treasury -> customer, kind CardPayment) only after the PSP approved.</item>
/// <item>Refund debits the wallet (customer -> treasury, kind Refund) when it is reserved; a PSP decline posts the reverse pair.</item>
/// <item>Void cancels an authorization that was never captured; no ledger rows.</item>
/// </list>
/// </summary>
public sealed class SqlCardPaymentService : ICardPaymentService
{
    private readonly ClearPayDbContext _db;
    private readonly IPspRouter _router;
    private readonly IIdempotencyStore _idempotency;
    private readonly IClock _clock;
    private readonly IWalletSummaryCache _cache;
    private readonly ILogger<SqlCardPaymentService> _logger;

    public SqlCardPaymentService(
        ClearPayDbContext db,
        IPspRouter router,
        IIdempotencyStore idempotency,
        IClock clock,
        IWalletSummaryCache cache,
        ILogger<SqlCardPaymentService> logger)
    {
        _db = db;
        _router = router;
        _idempotency = idempotency;
        _clock = clock;
        _cache = cache;
        _logger = logger;
    }

    public async Task<PaymentResult> CreateAsync(CreateCardPaymentCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return PaymentResult.Fail(PaymentResultKind.MissingKey);

        var key = command.IdempotencyKey.Trim();
        var hash = Hash("create", command.ThreeDsSessionId.ToString("N"), command.Provider ?? string.Empty, command.Capture.ToString());
        var existing = await _idempotency.FindAsync(key, "payment", cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await ReplayCreateAsync(existing, hash, command, cancellationToken).ConfigureAwait(false);

        var session = await _db.ThreeDsSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == command.ThreeDsSessionId && s.UserId == command.UserId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
            return PaymentResult.Fail(PaymentResultKind.NotFound);
        if (session.Status != ThreeDsStatus.Authenticated)
            return PaymentResult.Fail(PaymentResultKind.ThreeDsNotAuthenticated);
        if (session.ConsumedByPaymentId is not null)
            return PaymentResult.Fail(PaymentResultKind.ThreeDsAlreadyUsed);

        var psp = _router.Resolve(command.Provider);
        if (psp is null)
            return PaymentResult.Fail(PaymentResultKind.UnknownProvider);

        var now = _clock.UtcNow;
        var payment = new CardPayment
        {
            Id = Guid.NewGuid(),
            UserId = command.UserId,
            ThreeDsSessionId = session.Id,
            Provider = psp.Name,
            Amount = session.Amount,
            CardLast4 = session.CardLast4,
            Status = CardPaymentStatus.Pending,
            CorrelationId = session.CorrelationId,
            CreatedAt = now,
            UpdatedAt = now
        };
        var authOp = NewOperation(payment, PaymentOperationType.Authorize, payment.Amount, $"{payment.Id:N}-auth", now);

        await using (var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false))
        {
            // One 3-D Secure authentication pays for exactly one payment.
            var claimed = await _db.ThreeDsSessions
                .Where(s => s.Id == session.Id && s.ConsumedByPaymentId == null && s.Status == ThreeDsStatus.Authenticated)
                .ExecuteUpdateAsync(set => set.SetProperty(s => s.ConsumedByPaymentId, payment.Id), cancellationToken)
                .ConfigureAwait(false);
            if (claimed != 1)
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return PaymentResult.Fail(PaymentResultKind.ThreeDsAlreadyUsed);
            }

            _db.CardPayments.Add(payment);
            _db.PaymentOperations.Add(authOp);
            await _idempotency.ReserveAsync(key, "payment", hash, payment.Id, cancellationToken).ConfigureAwait(false);
            if (!await TrySaveAsync(cancellationToken).ConfigureAwait(false))
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                var raced = await _idempotency.FindAsync(key, "payment", cancellationToken).ConfigureAwait(false);
                return raced is null
                    ? PaymentResult.Fail(PaymentResultKind.Replay)
                    : await ReplayCreateAsync(raced, hash, command, cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        _db.ChangeTracker.Clear();
        return await DriveAuthorizationAsync(payment.Id, command.Capture, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PaymentResult> CaptureAsync(PaymentActionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return PaymentResult.Fail(PaymentResultKind.MissingKey);
        var payment = await LoadOwnedAsync(command, cancellationToken).ConfigureAwait(false);
        if (payment is null)
            return PaymentResult.Fail(PaymentResultKind.NotFound);

        var key = command.IdempotencyKey.Trim();
        var hash = Hash("capture", payment.Id.ToString("N"));
        var existing = await _idempotency.FindAsync(key, "capture", cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await ReplayActionAsync(existing, hash, cancellationToken).ConfigureAwait(false);

        if (payment.Status != CardPaymentStatus.Authorized)
            return PaymentResult.Fail(PaymentResultKind.InvalidState, await ViewAsync(payment.Id, cancellationToken).ConfigureAwait(false));

        await _idempotency.ReserveAsync(key, "capture", hash, payment.Id, cancellationToken).ConfigureAwait(false);
        if (!await TrySaveAsync(cancellationToken).ConfigureAwait(false))
            return PaymentResult.Fail(PaymentResultKind.Replay, await ViewAsync(payment.Id, cancellationToken).ConfigureAwait(false));

        return await DriveCaptureAsync(payment.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PaymentResult> VoidAsync(PaymentActionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return PaymentResult.Fail(PaymentResultKind.MissingKey);
        var payment = await LoadOwnedAsync(command, cancellationToken).ConfigureAwait(false);
        if (payment is null)
            return PaymentResult.Fail(PaymentResultKind.NotFound);

        var key = command.IdempotencyKey.Trim();
        var hash = Hash("void", payment.Id.ToString("N"));
        var existing = await _idempotency.FindAsync(key, "void", cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await ReplayActionAsync(existing, hash, cancellationToken).ConfigureAwait(false);

        if (!payment.CanVoid)
            return PaymentResult.Fail(PaymentResultKind.InvalidState, await ViewAsync(payment.Id, cancellationToken).ConfigureAwait(false));

        var now = _clock.UtcNow;
        var op = NewOperation(payment, PaymentOperationType.Void, payment.Amount, $"{payment.Id:N}-void", now);
        _db.PaymentOperations.Add(op);
        await _idempotency.ReserveAsync(key, "void", hash, payment.Id, cancellationToken).ConfigureAwait(false);
        if (!await TrySaveAsync(cancellationToken).ConfigureAwait(false))
            return PaymentResult.Fail(PaymentResultKind.Replay, await ViewAsync(payment.Id, cancellationToken).ConfigureAwait(false));

        var psp = _router.Resolve(payment.Provider)!;
        var answer = await psp.VoidAsync(
            new PspFollowUpRequest(op.ProviderIdempotencyKey, payment.ProviderReference!, payment.Amount, op.CorrelationId),
            cancellationToken).ConfigureAwait(false);

        return await RecordAsync(payment.Id, op.Id, answer, async (p, now2) =>
        {
            if (!p.CanVoid)
                return false;
            p.MarkVoided(now2);
            AddEvent(p, "clearpay.payment.voided", p.Amount, answer.Reference, now2);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PaymentResult> RefundAsync(PaymentActionCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return PaymentResult.Fail(PaymentResultKind.MissingKey);
        var amount = command.Amount ?? 0m;
        if (amount <= 0m || decimal.Round(amount, 2) != amount)
            return PaymentResult.Fail(PaymentResultKind.InvalidAmount);

        var key = command.IdempotencyKey.Trim();
        var hash = Hash("refund", command.PaymentId.ToString("N"), amount.ToString("F2", CultureInfo.InvariantCulture));
        var existing = await _idempotency.FindAsync(key, "refund", cancellationToken).ConfigureAwait(false);
        if (existing is not null)
        {
            if (existing.RequestHash != hash)
                return PaymentResult.Fail(PaymentResultKind.KeyPayloadMismatch);
            var replayed = await _db.PaymentOperations.AsNoTracking()
                .FirstOrDefaultAsync(o => o.Id == existing.ResourceId, cancellationToken).ConfigureAwait(false);
            return new PaymentResult(PaymentResultKind.Replay,
                replayed is null ? null : await ViewAsync(replayed.PaymentId, cancellationToken).ConfigureAwait(false),
                existing.ResourceId);
        }

        var now = _clock.UtcNow;
        CardPayment? payment;
        PaymentOperation op;
        await using (var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false))
        {
            payment = await _db.CardPayments
                .FirstOrDefaultAsync(p => p.Id == command.PaymentId && p.UserId == command.UserId, cancellationToken)
                .ConfigureAwait(false);
            if (payment is null)
                return PaymentResult.Fail(PaymentResultKind.NotFound);
            if (payment.Status is not (CardPaymentStatus.Captured or CardPaymentStatus.PartiallyRefunded))
                return PaymentResult.Fail(PaymentResultKind.InvalidState, await ViewAsync(payment.Id, cancellationToken).ConfigureAwait(false));
            if (!payment.CanRefund(amount))
                return PaymentResult.Fail(PaymentResultKind.ExceedsRefundable, await ViewAsync(payment.Id, cancellationToken).ConfigureAwait(false));

            var customer = await WalletOfAsync(payment.UserId, cancellationToken).ConfigureAwait(false);
            var treasury = await WalletOfAsync(Treasury.UserId, cancellationToken).ConfigureAwait(false);
            var net = await _db.LedgerEntries.Where(e => e.WalletId == customer.Id).Select(e => e.Amount)
                .ToListAsync(cancellationToken).ConfigureAwait(false);
            if (LedgerPair.WouldGoNegative(net.Sum(), decimal.Negate(amount)))
                return PaymentResult.Fail(PaymentResultKind.InsufficientFunds, await ViewAsync(payment.Id, cancellationToken).ConfigureAwait(false));

            payment.ReserveRefund(amount, now);
            op = NewOperation(payment, PaymentOperationType.Refund, amount, string.Empty, now);
            op.ProviderIdempotencyKey = $"{op.Id:N}-refund";
            _db.PaymentOperations.Add(op);
            var (debit, credit) = LedgerPair.Create(customer.Id, treasury.Id, amount, op.CorrelationId,
                LedgerEntryKind.Refund, description: $"Card refund {payment.Provider} ****{payment.CardLast4}", at: now);
            _db.LedgerEntries.AddRange(debit, credit);
            AddAudit(payment, payment.UserId, "payment.refund.reserved", amount, op.CorrelationId, now);
            await _idempotency.ReserveAsync(key, "refund", hash, op.Id, cancellationToken).ConfigureAwait(false);
            if (!await TrySaveAsync(cancellationToken).ConfigureAwait(false))
            {
                await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
                return PaymentResult.Fail(PaymentResultKind.Replay);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        _db.ChangeTracker.Clear();
        await _cache.InvalidateAsync(payment.UserId, cancellationToken).ConfigureAwait(false);

        var psp = _router.Resolve(payment.Provider)!;
        var answer = await psp.RefundAsync(
            new PspFollowUpRequest(op.ProviderIdempotencyKey, payment.ProviderReference!, amount, op.CorrelationId),
            cancellationToken).ConfigureAwait(false);

        if (answer.Outcome == PspOutcome.Declined)
        {
            // Give the money back: reverse pair, release the reservation.
            await CompensateRefundAsync(payment.Id, op.Id, amount, answer.DeclineCode, cancellationToken).ConfigureAwait(false);
            return new PaymentResult(PaymentResultKind.Declined, await ViewAsync(payment.Id, cancellationToken).ConfigureAwait(false),
                op.Id, answer.DeclineCode);
        }

        return await RecordAsync(payment.Id, op.Id, answer, async (p, now2) =>
        {
            AddEvent(p, "clearpay.payment.refunded", amount, answer.Reference, now2);
            return true;
        }, cancellationToken).ConfigureAwait(false);
    }

    public Task<CardPaymentView?> GetAsync(Guid paymentId, string userId, CancellationToken cancellationToken = default) =>
        ViewAsync(paymentId, cancellationToken, userId);

    private async Task<PaymentResult> DriveAuthorizationAsync(Guid paymentId, bool capture, CancellationToken cancellationToken)
    {
        var payment = await _db.CardPayments.AsNoTracking().FirstAsync(p => p.Id == paymentId, cancellationToken).ConfigureAwait(false);
        var op = await _db.PaymentOperations.AsNoTracking()
            .FirstAsync(o => o.PaymentId == paymentId && o.Type == PaymentOperationType.Authorize, cancellationToken)
            .ConfigureAwait(false);
        var session = await _db.ThreeDsSessions.AsNoTracking().FirstAsync(s => s.Id == payment.ThreeDsSessionId, cancellationToken)
            .ConfigureAwait(false);

        var psp = _router.Resolve(payment.Provider)!;
        var answer = await psp.AuthorizeAsync(
            new PspAuthorizeRequest(op.ProviderIdempotencyKey, payment.Amount, "TRY", payment.CardLast4,
                session.Eci ?? "05", session.AuthenticationValue ?? string.Empty, payment.CorrelationId),
            cancellationToken).ConfigureAwait(false);

        var result = await RecordAsync(paymentId, op.Id, answer, async (p, now) =>
        {
            if (p.Status != CardPaymentStatus.Pending)
                return false;
            if (answer.Outcome == PspOutcome.Approved)
            {
                p.MarkAuthorized(answer.Reference!, now);
                AddEvent(p, "clearpay.payment.authorized", p.Amount, answer.Reference, now);
            }
            else
            {
                p.MarkDeclined(answer.DeclineCode, now);
                AddEvent(p, "clearpay.payment.declined", p.Amount, null, now);
            }

            return true;
        }, cancellationToken).ConfigureAwait(false);

        if (answer.Outcome == PspOutcome.Approved && capture)
            return await DriveCaptureAsync(paymentId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private async Task<PaymentResult> DriveCaptureAsync(Guid paymentId, CancellationToken cancellationToken)
    {
        var payment = await _db.CardPayments.AsNoTracking().FirstAsync(p => p.Id == paymentId, cancellationToken).ConfigureAwait(false);
        var providerKey = $"{payment.Id:N}-capture";
        var op = await _db.PaymentOperations.AsNoTracking()
            .FirstOrDefaultAsync(o => o.PaymentId == paymentId && o.Type == PaymentOperationType.Capture, cancellationToken)
            .ConfigureAwait(false);
        if (op is null)
        {
            op = NewOperation(payment, PaymentOperationType.Capture, payment.Amount, providerKey, _clock.UtcNow);
            op.CorrelationId = payment.CorrelationId;
            _db.PaymentOperations.Add(op);
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            _db.ChangeTracker.Clear();
        }

        var psp = _router.Resolve(payment.Provider)!;
        var answer = await psp.CaptureAsync(
            new PspFollowUpRequest(providerKey, payment.ProviderReference!, payment.Amount, payment.CorrelationId),
            cancellationToken).ConfigureAwait(false);

        var result = await RecordAsync(paymentId, op.Id, answer, async (p, now) =>
        {
            if (answer.Outcome != PspOutcome.Approved)
                return true;
            if (!p.CanCapture)
                return false;
            p.MarkCaptured(now);
            await PostCaptureAsync(p, now).ConfigureAwait(false);
            AddEvent(p, "clearpay.payment.captured", p.Amount, answer.Reference, now);
            return true;
        }, cancellationToken).ConfigureAwait(false);
        if (answer.Outcome == PspOutcome.Approved)
            await _cache.InvalidateAsync(payment.UserId, cancellationToken).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Writes the PSP answer: operation status + reference, and (when <paramref name="apply"/> agrees)
    /// the payment change with its ledger/outbox rows - all in one Serializable transaction.
    /// </summary>
    private async Task<PaymentResult> RecordAsync(
        Guid paymentId,
        Guid operationId,
        PspResult answer,
        Func<CardPayment, DateTimeOffset, Task<bool>> apply,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        if (answer.Outcome == PspOutcome.TimedOut)
        {
            _logger.LogWarning("PSP timed out for payment {PaymentId} operation {OperationId}; left Pending for retry/reconciliation",
                paymentId, operationId);
            return new PaymentResult(PaymentResultKind.TimedOut, await ViewAsync(paymentId, cancellationToken).ConfigureAwait(false), operationId);
        }

        await using (var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false))
        {
            var payment = await _db.CardPayments.FirstAsync(p => p.Id == paymentId, cancellationToken).ConfigureAwait(false);
            var op = await _db.PaymentOperations.FirstAsync(o => o.Id == operationId, cancellationToken).ConfigureAwait(false);
            if (op.Status == PaymentOperationStatus.Pending)
            {
                op.Status = answer.Outcome == PspOutcome.Approved ? PaymentOperationStatus.Succeeded : PaymentOperationStatus.Failed;
                op.ProviderReference = answer.Reference;
                op.UpdatedAt = now;
                await apply(payment, now).ConfigureAwait(false);
                await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }

            await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        }

        _db.ChangeTracker.Clear();
        var view = await ViewAsync(paymentId, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Payment {PaymentId} operation {OperationId}: PSP {Outcome} -> payment {Status}",
            paymentId, operationId, answer.Outcome, view?.Status);
        return answer.Outcome == PspOutcome.Approved
            ? new PaymentResult(PaymentResultKind.Ok, view, operationId)
            : new PaymentResult(PaymentResultKind.Declined, view, operationId, answer.DeclineCode);
    }

    private async Task CompensateRefundAsync(Guid paymentId, Guid operationId, decimal amount, string? code, CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(IsolationLevel.Serializable, cancellationToken).ConfigureAwait(false);
        var op = await _db.PaymentOperations.FirstAsync(o => o.Id == operationId, cancellationToken).ConfigureAwait(false);
        if (op.Status != PaymentOperationStatus.Pending)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        var payment = await _db.CardPayments.FirstAsync(p => p.Id == paymentId, cancellationToken).ConfigureAwait(false);
        payment.ReleaseRefund(amount, now);
        op.Status = PaymentOperationStatus.Failed;
        op.UpdatedAt = now;
        var customer = await WalletOfAsync(payment.UserId, cancellationToken).ConfigureAwait(false);
        var treasury = await WalletOfAsync(Treasury.UserId, cancellationToken).ConfigureAwait(false);
        var (debit, credit) = LedgerPair.Create(treasury.Id, customer.Id, amount, op.CorrelationId,
            LedgerEntryKind.Refund, description: $"Refund declined by PSP ({code}), reversed", at: now);
        _db.LedgerEntries.AddRange(debit, credit);
        AddEvent(payment, "clearpay.payment.refund_failed", amount, null, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
        await _cache.InvalidateAsync(payment.UserId, cancellationToken).ConfigureAwait(false);
    }

    private async Task PostCaptureAsync(CardPayment payment, DateTimeOffset now)
    {
        var customer = await WalletOfAsync(payment.UserId, CancellationToken.None).ConfigureAwait(false);
        var treasury = await WalletOfAsync(Treasury.UserId, CancellationToken.None).ConfigureAwait(false);
        var (debit, credit) = LedgerPair.Create(treasury.Id, customer.Id, payment.Amount, payment.CorrelationId,
            LedgerEntryKind.CardPayment, description: $"Card payment {payment.Provider} ****{payment.CardLast4}", at: now);
        _db.LedgerEntries.AddRange(debit, credit);
        AddAudit(payment, payment.UserId, "payment.captured", payment.Amount, payment.CorrelationId, now);
    }

    /// <summary>Existing wallet, or a new one staged on the current unit of work (saved with the ledger rows).</summary>
    private async Task<Wallet> WalletOfAsync(string userId, CancellationToken cancellationToken)
    {
        var wallet = _db.Wallets.Local.FirstOrDefault(w => w.UserId == userId)
            ?? await _db.Wallets.FirstOrDefaultAsync(w => w.UserId == userId, cancellationToken).ConfigureAwait(false);
        if (wallet is not null)
            return wallet;
        wallet = new Wallet { Id = Guid.NewGuid(), UserId = userId, IsFrozen = false, CreatedAt = _clock.UtcNow };
        _db.Wallets.Add(wallet);
        return wallet;
    }

    private async Task<PaymentResult> ReplayCreateAsync(
        IdempotencyLookup existing, string hash, CreateCardPaymentCommand command, CancellationToken cancellationToken)
    {
        if (existing.RequestHash != hash || existing.ResourceId is null)
            return PaymentResult.Fail(PaymentResultKind.KeyPayloadMismatch);
        var view = await ViewAsync(existing.ResourceId.Value, cancellationToken).ConfigureAwait(false);
        if (view is { Status: CardPaymentStatus.Pending })
        {
            // The first attempt timed out at the PSP: drive it again with the same provider key.
            return await DriveAuthorizationAsync(view.PaymentId, command.Capture, cancellationToken).ConfigureAwait(false);
        }

        var captureStuck = view?.Operations.Any(o => o.Type == PaymentOperationType.Capture && o.Status == PaymentOperationStatus.Pending) == true;
        if (view is { Status: CardPaymentStatus.Authorized } && command.Capture && captureStuck)
            return await DriveCaptureAsync(view.PaymentId, cancellationToken).ConfigureAwait(false);

        return new PaymentResult(PaymentResultKind.Replay, view);
    }

    private async Task<PaymentResult> ReplayActionAsync(IdempotencyLookup existing, string hash, CancellationToken cancellationToken)
    {
        if (existing.RequestHash != hash || existing.ResourceId is null)
            return PaymentResult.Fail(PaymentResultKind.KeyPayloadMismatch);
        var view = await ViewAsync(existing.ResourceId.Value, cancellationToken).ConfigureAwait(false);
        var pendingCapture = view?.Operations.Any(o => o.Type == PaymentOperationType.Capture && o.Status == PaymentOperationStatus.Pending) == true;
        if (view is { Status: CardPaymentStatus.Authorized } && pendingCapture)
            return await DriveCaptureAsync(view.PaymentId, cancellationToken).ConfigureAwait(false);
        return new PaymentResult(PaymentResultKind.Replay, view);
    }

    private Task<CardPayment?> LoadOwnedAsync(PaymentActionCommand command, CancellationToken cancellationToken) =>
        _db.CardPayments.AsNoTracking()
            .FirstOrDefaultAsync(p => p.Id == command.PaymentId && p.UserId == command.UserId, cancellationToken);

    private async Task<bool> TrySaveAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (DbUpdateException ex) when (UniqueConstraint.IsDuplicateKey(ex))
        {
            _db.ChangeTracker.Clear();
            return false;
        }
    }

    private async Task<CardPaymentView?> ViewAsync(Guid paymentId, CancellationToken cancellationToken, string? userId = null)
    {
        var p = await _db.CardPayments.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == paymentId && (userId == null || x.UserId == userId), cancellationToken)
            .ConfigureAwait(false);
        if (p is null)
            return null;
        var ops = await _db.PaymentOperations.AsNoTracking().Where(o => o.PaymentId == paymentId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);
        return new CardPaymentView(p.Id, p.Status, p.Provider, p.ProviderReference, p.Amount, p.CapturedAmount,
            p.RefundedAmount, p.CardLast4, p.CorrelationId,
            ops.OrderBy(o => o.CreatedAt).ThenBy(o => o.Type)
                .Select(o => new PaymentOperationView(o.Id, o.Type, o.Status, o.Amount, o.ProviderReference, o.CreatedAt))
                .ToList());
    }

    private static PaymentOperation NewOperation(
        CardPayment payment, PaymentOperationType type, decimal amount, string providerKey, DateTimeOffset now) => new()
    {
        Id = Guid.NewGuid(),
        PaymentId = payment.Id,
        Type = type,
        Status = PaymentOperationStatus.Pending,
        Amount = amount,
        Provider = payment.Provider,
        ProviderIdempotencyKey = providerKey,
        CorrelationId = type == PaymentOperationType.Refund ? Guid.NewGuid() : payment.CorrelationId,
        CreatedAt = now,
        UpdatedAt = now
    };

    private void AddAudit(CardPayment p, string actor, string action, decimal amount, Guid correlationId, DateTimeOffset now) =>
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorUserId = actor,
            Action = action,
            CorrelationId = correlationId,
            Details = JsonSerializer.Serialize(new { paymentId = p.Id, provider = p.Provider, amount }),
            CreatedAt = now
        });

    private void AddEvent(CardPayment p, string type, decimal amount, string? providerReference, DateTimeOffset now) =>
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = JsonSerializer.Serialize(new
            {
                paymentId = p.Id,
                correlationId = p.CorrelationId,
                userId = p.UserId,
                provider = p.Provider,
                providerReference,
                amount,
                capturedAmount = p.CapturedAmount,
                refundedAmount = p.RefundedAmount,
                status = p.Status.ToString(),
                occurredAt = now
            }),
            CorrelationId = p.CorrelationId,
            OccurredAt = now,
            Status = OutboxStatus.Pending
        });

    private static string Hash(params string[] parts) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join('\n', parts))));
}
