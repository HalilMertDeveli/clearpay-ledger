using ClearPay.Application.Payments;
using ClearPay.Application.Ports;
using ClearPay.Domain.Payments;
using ClearPay.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ClearPay.Infrastructure.Payments.Psp;

/// <summary>
/// Shared "bank side" of the three fake PSPs: provider-side idempotency and the journal that
/// settlement files are generated from. Uses its own DbContext scope so a simulator write is
/// never part of the payment service's transaction (a real PSP is a different system).
/// </summary>
public sealed class SimulatedPspCore
{
    private readonly IServiceScopeFactory _scopes;
    private readonly IClock _clock;

    public SimulatedPspCore(IServiceScopeFactory scopes, IClock clock)
    {
        _scopes = scopes;
        _clock = clock;
    }

    /// <summary>The bank's decision for one call; computed only the first time a key is seen.</summary>
    public sealed record Decision(bool Approved, string? DeclineCode, bool LoseResponse);

    public async Task<PspResult> ExecuteAsync(
        string provider,
        PaymentOperationType type,
        string idempotencyKey,
        string? authorizationReference,
        decimal amount,
        Guid correlationId,
        Func<SimPspTransaction?, Decision> decide,
        CancellationToken cancellationToken)
    {
        using var scope = _scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ClearPayDbContext>();

        var existing = await FindAsync(db, provider, idempotencyKey, cancellationToken).ConfigureAwait(false);
        if (existing is not null)
            return Replay(existing);

        SimPspTransaction? authorization = null;
        if (authorizationReference is not null)
        {
            authorization = await db.SimPspTransactions.AsNoTracking()
                .FirstOrDefaultAsync(
                    t => t.Provider == provider && t.Reference == authorizationReference && t.Type == PaymentOperationType.Authorize,
                    cancellationToken)
                .ConfigureAwait(false);
            if (authorization is null || !authorization.Approved)
                return PspResult.Declined("unknown-authorization");
        }

        var decision = decide(authorization);
        var row = new SimPspTransaction
        {
            Id = Guid.NewGuid(),
            Provider = provider,
            IdempotencyKey = idempotencyKey,
            Type = type,
            AuthorizationReference = authorizationReference,
            Reference = Truncate($"{provider.ToUpperInvariant()}-{type.ToString().ToUpperInvariant()}-{Guid.NewGuid():N}", 48),
            Amount = amount,
            Approved = decision.Approved,
            DeclineCode = decision.DeclineCode,
            ResponseLost = decision.LoseResponse,
            CorrelationId = correlationId,
            CreatedAt = _clock.UtcNow
        };
        db.SimPspTransactions.Add(row);
        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (UniqueConstraint.IsDuplicateKey(ex))
        {
            db.ChangeTracker.Clear();
            var raced = await FindAsync(db, provider, idempotencyKey, cancellationToken).ConfigureAwait(false);
            return raced is null ? PspResult.TimedOut() : Replay(raced);
        }

        // Lost response: the bank did the work, the caller never hears about it.
        if (decision.LoseResponse)
            return PspResult.TimedOut();

        return row.Approved ? PspResult.Approved(row.Reference) : PspResult.Declined(row.DeclineCode ?? "declined");
    }

    /// <summary>A retry with the same key gets the stored answer, including after a lost response.</summary>
    private static PspResult Replay(SimPspTransaction t) =>
        t.Approved ? PspResult.Approved(t.Reference) : PspResult.Declined(t.DeclineCode ?? "declined");

    private static Task<SimPspTransaction?> FindAsync(
        ClearPayDbContext db, string provider, string key, CancellationToken cancellationToken) =>
        db.SimPspTransactions.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Provider == provider && t.IdempotencyKey == key, cancellationToken);

    /// <summary>Cents of the amount, used by the simulators' documented fault triggers.</summary>
    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];

    public static int Cents(decimal amount) => (int)(decimal.Round(amount * 100m) % 100m);
}
