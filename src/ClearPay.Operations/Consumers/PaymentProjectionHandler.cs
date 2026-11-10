using System.Text.Json;
using ClearPay.Messaging;
using ClearPay.Operations.Data;
using Microsoft.EntityFrameworkCore;

namespace ClearPay.Operations.Consumers;

/// <summary>
/// Keeps <c>ops.PaymentOperation</c> in step with the ledger's <c>clearpay.payment.operation.recorded</c>
/// events. Inbox-deduplicated; a late "Pending" snapshot never overwrites a final Succeeded/Failed one.
/// </summary>
public sealed class PaymentProjectionHandler : IMessageHandler
{
    public const string ConsumerName = "clearpay.ops.payment-projection";

    private readonly InboxProcessor<OperationsDbContext> _inbox;
    private readonly TimeProvider _time;

    public PaymentProjectionHandler(InboxProcessor<OperationsDbContext> inbox, TimeProvider time)
    {
        _inbox = inbox;
        _time = time;
    }

    public string Consumer => ConsumerName;

    public IReadOnlyCollection<string> RoutingKeys { get; } = new[] { "clearpay.payment.operation.recorded" };

    public Task<bool> HandleAsync(InboundMessage message, CancellationToken cancellationToken)
    {
        Snapshot snapshot;
        try
        {
            snapshot = JsonSerializer.Deserialize<Snapshot>(message.Payload, Json.Options)
                ?? throw new JsonException("empty payload");
            if (snapshot.OperationId == Guid.Empty || string.IsNullOrEmpty(snapshot.Type) || string.IsNullOrEmpty(snapshot.Status))
                throw new JsonException("missing fields");
        }
        catch (JsonException ex)
        {
            throw new PoisonMessageException("Unreadable payment.operation.recorded payload", ex);
        }

        return _inbox.ProcessAsync(Consumer, message, async (db, _, ct) =>
        {
            var row = await db.PaymentOperations.FirstOrDefaultAsync(p => p.OperationId == snapshot.OperationId, ct).ConfigureAwait(false);
            if (row is null)
            {
                row = new PaymentOperationProjection { OperationId = snapshot.OperationId };
                db.PaymentOperations.Add(row);
            }
            else if (IsFinal(row.Status) && !IsFinal(snapshot.Status))
            {
                return; // out-of-order delivery: keep the final state
            }

            row.PaymentId = snapshot.PaymentId;
            row.UserId = snapshot.UserId ?? string.Empty;
            row.Type = snapshot.Type!;
            row.Status = snapshot.Status!;
            row.Provider = snapshot.Provider ?? string.Empty;
            row.ProviderReference = snapshot.ProviderReference ?? row.ProviderReference;
            row.MerchantReference = snapshot.MerchantReference;
            row.Amount = snapshot.Amount;
            row.CreatedAt = snapshot.CreatedAt;
            row.UpdatedAt = snapshot.UpdatedAt;
        }, _time.GetUtcNow(), cancellationToken);
    }

    private static bool IsFinal(string status) => status is "Succeeded" or "Failed";

    private sealed record Snapshot(
        Guid OperationId,
        Guid PaymentId,
        string? UserId,
        string? Type,
        string? Status,
        string? Provider,
        string? ProviderReference,
        Guid MerchantReference,
        decimal Amount,
        DateTimeOffset CreatedAt,
        DateTimeOffset UpdatedAt);
}

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);
}
