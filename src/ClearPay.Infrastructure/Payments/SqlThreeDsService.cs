using System.Text.Json;
using ClearPay.Application.Payments;
using ClearPay.Application.Ports;
using ClearPay.Domain.Ledger;
using ClearPay.Domain.Payments;
using ClearPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ClearPay.Infrastructure.Payments;

/// <summary>
/// Gateway side of the fake 3-D Secure flow. Every state change is a compare-and-set
/// (<c>UPDATE ... WHERE Id = @id AND Status = @expected</c>) committed together with its audit and
/// outbox rows, so a late ACS callback and the expiry job cannot both win.
/// </summary>
public sealed class SqlThreeDsService : IThreeDsService
{
    public const string IdempotencyScope = "3ds";
    private const string DeadlineReason = "Challenge deadline passed";

    private readonly ClearPayDbContext _db;
    private readonly IIdempotencyStore _idempotency;
    private readonly IClock _clock;
    private readonly ThreeDsOptions _options;
    private readonly ILogger<SqlThreeDsService> _logger;

    public SqlThreeDsService(
        ClearPayDbContext db,
        IIdempotencyStore idempotency,
        IClock clock,
        ThreeDsOptions options,
        ILogger<SqlThreeDsService> logger)
    {
        _db = db;
        _idempotency = idempotency;
        _clock = clock;
        _options = options;
        _logger = logger;
    }

    public async Task<ThreeDsInitiateOutcome> InitiateAsync(
        ThreeDsInitiateCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.IsNullOrWhiteSpace(command.IdempotencyKey))
            return ThreeDsInitiateOutcome.Fail(ThreeDsInitiateKind.MissingKey);
        if (command.Amount <= 0m || decimal.Round(command.Amount, 2) != command.Amount || command.Amount > 1_000_000m)
            return ThreeDsInitiateOutcome.Fail(ThreeDsInitiateKind.InvalidAmount);
        if (!ThreeDsTestCards.IsLuhnValid(command.CardNumber))
            return ThreeDsInitiateOutcome.Fail(ThreeDsInitiateKind.InvalidCard);

        var key = command.IdempotencyKey.Trim();
        var existing = await _idempotency.FindAsync(key, IdempotencyScope, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return await ReplayAsync(existing.ResourceId, cancellationToken).ConfigureAwait(false);

        var now = _clock.UtcNow;
        var last4 = ThreeDsTestCards.Last4(command.CardNumber);
        var session = new ThreeDsSession
        {
            Id = Guid.NewGuid(),
            UserId = command.UserId,
            Amount = command.Amount,
            Currency = "TRY",
            CardLast4 = last4,
            Status = ThreeDsStatus.Initiated,
            CorrelationId = Guid.NewGuid(),
            CreatedAt = now,
            UpdatedAt = now,
            ExpiresAt = now + _options.ChallengeTimeout
        };

        // Fake directory-server decision: one test card is refused frictionlessly, every other card is challenged.
        if (last4 == ThreeDsTestCards.DeclinedLast4)
            session.TryMoveTo(ThreeDsStatus.Failed, now, "Issuer refused authentication (frictionless N)");
        else
            session.TryMoveTo(ThreeDsStatus.Challenge, now);

        _db.ThreeDsSessions.Add(session);
        await _idempotency.ReserveAsync(key, IdempotencyScope, null, session.Id, cancellationToken).ConfigureAwait(false);
        AddAudit(session, command.UserId, "3ds.initiated", now);
        if (session.Status == ThreeDsStatus.Failed)
            AddEvent(session, now);

        try
        {
            await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (UniqueConstraint.IsDuplicateKey(ex))
        {
            _db.ChangeTracker.Clear();
            var raced = await _idempotency.FindAsync(key, IdempotencyScope, cancellationToken).ConfigureAwait(false);
            return await ReplayAsync(raced?.ResourceId, cancellationToken).ConfigureAwait(false);
        }

        _logger.LogInformation(
            "3DS session {SessionId} started as {Status} for {Amount} TRY (correlation {CorrelationId})",
            session.Id, session.Status, session.Amount, session.CorrelationId);
        return new ThreeDsInitiateOutcome(ThreeDsInitiateKind.Created, session.Id, session.Status, AcsUrlFor(session));
    }

    public async Task<ThreeDsSessionView?> GetAsync(Guid sessionId, string userId, CancellationToken cancellationToken = default)
    {
        var s = await _db.ThreeDsSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == sessionId && x.UserId == userId, cancellationToken)
            .ConfigureAwait(false);
        if (s is null)
            return null;

        if (s.IsExpired(_clock.UtcNow))
        {
            await TransitionAsync(s, ThreeDsStatus.TimedOut, DeadlineReason, null, null, null, cancellationToken)
                .ConfigureAwait(false);
            s = await _db.ThreeDsSessions.AsNoTracking().FirstAsync(x => x.Id == sessionId, cancellationToken)
                .ConfigureAwait(false);
        }

        return ToView(s);
    }

    public async Task<ThreeDsCallbackOutcome> HandleCallbackAsync(
        AcsChallengeResult result,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        var canonical = ThreeDsSigner.CanonicalChallengeResult(result);
        if (!ThreeDsSigner.Verify(canonical, result.Signature, _options.AcsSharedSecret))
        {
            _logger.LogWarning("3DS callback rejected: bad signature for session {SessionId}", result.SessionId);
            return new ThreeDsCallbackOutcome(ThreeDsCallbackKind.InvalidSignature, result.SessionId, null);
        }

        var now = _clock.UtcNow;
        var issuedAt = DateTimeOffset.FromUnixTimeSeconds(result.IssuedAtUnix);
        if ((now - issuedAt).Duration() > _options.CallbackMaxAge)
        {
            _logger.LogWarning("3DS callback rejected: stale result for session {SessionId}", result.SessionId);
            return new ThreeDsCallbackOutcome(ThreeDsCallbackKind.Stale, result.SessionId, null);
        }

        var session = await _db.ThreeDsSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == result.SessionId, cancellationToken)
            .ConfigureAwait(false);
        if (session is null)
            return new ThreeDsCallbackOutcome(ThreeDsCallbackKind.NotFound, result.SessionId, null);

        if (session.AcsNonce is not null && session.AcsNonce == result.Nonce)
            return new ThreeDsCallbackOutcome(ThreeDsCallbackKind.AlreadyApplied, session.Id, session.Status);

        if (session.IsTerminal)
            return new ThreeDsCallbackOutcome(ThreeDsCallbackKind.Conflict, session.Id, session.Status);

        if (session.IsExpired(now))
        {
            await TransitionAsync(session, ThreeDsStatus.TimedOut, DeadlineReason, null, null, null, cancellationToken)
                .ConfigureAwait(false);
            return new ThreeDsCallbackOutcome(ThreeDsCallbackKind.Expired, session.Id, ThreeDsStatus.TimedOut);
        }

        var authenticated = result.TransStatus == "Y" && !string.IsNullOrEmpty(result.AuthenticationValue);
        var target = authenticated ? ThreeDsStatus.Authenticated : ThreeDsStatus.Failed;
        var applied = await TransitionAsync(
                session,
                target,
                authenticated ? null : "Cardholder was not authenticated by the ACS",
                result.Eci,
                authenticated ? result.AuthenticationValue : null,
                result.Nonce,
                cancellationToken)
            .ConfigureAwait(false);
        if (applied)
            return new ThreeDsCallbackOutcome(ThreeDsCallbackKind.Applied, session.Id, target);

        var current = await _db.ThreeDsSessions.AsNoTracking()
            .FirstAsync(x => x.Id == session.Id, cancellationToken)
            .ConfigureAwait(false);
        return current.AcsNonce == result.Nonce
            ? new ThreeDsCallbackOutcome(ThreeDsCallbackKind.AlreadyApplied, current.Id, current.Status)
            : new ThreeDsCallbackOutcome(ThreeDsCallbackKind.Conflict, current.Id, current.Status);
    }

    public async Task<int> ExpireStaleAsync(CancellationToken cancellationToken = default)
    {
        var now = _clock.UtcNow;
        // DateTimeOffset comparisons are filtered client-side so the same code runs on SQLite (tests).
        var open = await _db.ThreeDsSessions.AsNoTracking()
            .Where(x => x.Status == ThreeDsStatus.Initiated || x.Status == ThreeDsStatus.Challenge)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var moved = 0;
        foreach (var session in open.Where(s => s.IsExpired(now)))
        {
            if (await TransitionAsync(session, ThreeDsStatus.TimedOut, DeadlineReason, null, null, null, cancellationToken)
                    .ConfigureAwait(false))
                moved++;
        }

        return moved;
    }

    /// <summary>Compare-and-set on Status plus audit and outbox rows in one transaction.</summary>
    private async Task<bool> TransitionAsync(
        ThreeDsSession snapshot,
        ThreeDsStatus target,
        string? reason,
        string? eci,
        string? authValue,
        string? nonce,
        CancellationToken cancellationToken)
    {
        var now = _clock.UtcNow;
        var expected = snapshot.Status;
        var after = new ThreeDsSession
        {
            Id = snapshot.Id,
            UserId = snapshot.UserId,
            Amount = snapshot.Amount,
            CardLast4 = snapshot.CardLast4,
            CorrelationId = snapshot.CorrelationId,
            Status = expected,
            Eci = eci
        };
        if (!after.TryMoveTo(target, now, reason))
            return false;

        var failure = after.FailureReason;
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        var affected = await _db.ThreeDsSessions
            .Where(x => x.Id == snapshot.Id && x.Status == expected)
            .ExecuteUpdateAsync(set => set
                    .SetProperty(x => x.Status, target)
                    .SetProperty(x => x.UpdatedAt, now)
                    .SetProperty(x => x.FailureReason, failure)
                    .SetProperty(x => x.Eci, eci)
                    .SetProperty(x => x.AuthenticationValue, authValue)
                    .SetProperty(x => x.AcsNonce, nonce),
                cancellationToken)
            .ConfigureAwait(false);
        if (affected != 1)
        {
            await tx.RollbackAsync(cancellationToken).ConfigureAwait(false);
            return false;
        }

        AddAudit(after, "acs", "3ds." + target.ToString().ToLowerInvariant(), now);
        AddEvent(after, now);
        await _db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(cancellationToken).ConfigureAwait(false);
        _db.ChangeTracker.Clear();
        _logger.LogInformation(
            "3DS session {SessionId} moved {From} -> {To} (correlation {CorrelationId})",
            snapshot.Id, expected, target, snapshot.CorrelationId);
        return true;
    }

    private async Task<ThreeDsInitiateOutcome> ReplayAsync(Guid? sessionId, CancellationToken cancellationToken)
    {
        if (sessionId is null)
            return new ThreeDsInitiateOutcome(ThreeDsInitiateKind.Replay, Guid.Empty, ThreeDsStatus.Failed, null);
        var s = await _db.ThreeDsSessions.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == sessionId.Value, cancellationToken)
            .ConfigureAwait(false);
        return s is null
            ? new ThreeDsInitiateOutcome(ThreeDsInitiateKind.Replay, sessionId.Value, ThreeDsStatus.Failed, null)
            : new ThreeDsInitiateOutcome(ThreeDsInitiateKind.Replay, s.Id, s.Status, AcsUrlFor(s));
    }

    private string? AcsUrlFor(ThreeDsSession s) =>
        s.Status == ThreeDsStatus.Challenge
            ? "/fake-acs?creq=" + Uri.EscapeDataString(
                ChallengeToken.Create(s.Id, s.Amount, s.CardLast4, s.ExpiresAt, _options.AcsSharedSecret))
            : null;

    private void AddAudit(ThreeDsSession s, string actor, string action, DateTimeOffset now) =>
        _db.AuditLogs.Add(new AuditLog
        {
            Id = Guid.NewGuid(),
            ActorUserId = actor,
            Action = action,
            CorrelationId = s.CorrelationId,
            Details = JsonSerializer.Serialize(new
            {
                sessionId = s.Id,
                status = s.Status.ToString(),
                amount = s.Amount,
                last4 = s.CardLast4
            }),
            CreatedAt = now
        });

    private void AddEvent(ThreeDsSession s, DateTimeOffset now) =>
        _db.OutboxMessages.Add(new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = "clearpay.3ds." + s.Status.ToString().ToLowerInvariant(),
            Payload = JsonSerializer.Serialize(new
            {
                sessionId = s.Id,
                correlationId = s.CorrelationId,
                userId = s.UserId,
                amount = s.Amount,
                status = s.Status.ToString(),
                eci = s.Eci,
                reason = s.FailureReason
            }),
            CorrelationId = s.CorrelationId,
            OccurredAt = now,
            Status = OutboxStatus.Pending
        });

    private static ThreeDsSessionView ToView(ThreeDsSession s) => new(
        s.Id, s.Status, s.Amount, s.Currency, s.CardLast4, s.Eci, s.FailureReason,
        s.CorrelationId, s.CreatedAt, s.ExpiresAt, s.UpdatedAt);
}
