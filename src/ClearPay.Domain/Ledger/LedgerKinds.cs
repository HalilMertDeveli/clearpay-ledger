namespace ClearPay.Domain.Ledger;

/// <summary>What the signed pair represents. Refund is a reverse pair, not a balance UPDATE.</summary>
public enum LedgerEntryKind
{
    Transfer = 0,
    TopUp = 1,
    Withdraw = 2,
    Refund = 3,

    /// <summary>Card payment captured through a PSP after 3-D Secure (treasury -> customer).</summary>
    CardPayment = 4,

    /// <summary>Promotion credit (promotions purse -> customer), posted by a message consumer.</summary>
    Cashback = 5
}

/// <summary>Transfer row status. Gateway pending/fail arrives in TASK-07; TASK-06 posts Completed in one commit.</summary>
public enum TransferStatus
{
    Pending = 0,
    Completed = 1,
    Failed = 2
}

/// <summary>
/// Outbox row lifecycle. Inserted in the same SQL transaction as the ledger pair (TASK-06).
/// Worker (TASK-11) moves Pending → Sent / Failed. Null ProcessedAt means unpublished.
/// </summary>
public enum OutboxStatus
{
    Pending = 0,
    Sent = 1,
    Failed = 2
}
