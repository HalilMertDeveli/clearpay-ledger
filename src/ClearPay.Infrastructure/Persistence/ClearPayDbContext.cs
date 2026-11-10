using ClearPay.Domain.Ledger;
using ClearPay.Domain.Payments;
using Microsoft.EntityFrameworkCore;

namespace ClearPay.Infrastructure.Persistence;

/// <summary>
/// SQL Server ledger store. Identity is <c>AppIdentityDbContext</c> on the same SQL Server (SQLite only in tests).
/// No Balance column. PageModels must not take this type — use Application ports.
/// </summary>
public sealed class ClearPayDbContext : DbContext
{
    public ClearPayDbContext(DbContextOptions<ClearPayDbContext> options)
        : base(options)
    {
    }

    public DbSet<Wallet> Wallets => Set<Wallet>();

    public DbSet<LedgerEntry> LedgerEntries => Set<LedgerEntry>();

    public DbSet<Transfer> Transfers => Set<Transfer>();

    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();

    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();

    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    public DbSet<LinkedInstrument> LinkedInstruments => Set<LinkedInstrument>();

    public DbSet<ThreeDsSession> ThreeDsSessions => Set<ThreeDsSession>();

    public DbSet<CardPayment> CardPayments => Set<CardPayment>();

    public DbSet<PaymentOperation> PaymentOperations => Set<PaymentOperation>();

    /// <summary>Fake PSPs' own journal ("sim" schema). Only the simulators write it.</summary>
    public DbSet<SimPspTransaction> SimPspTransactions => Set<SimPspTransaction>();



    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(
            typeof(ClearPayDbContext).Assembly,
            type => type.Namespace == "ClearPay.Infrastructure.Persistence.Configurations");
        ClearPay.Messaging.InboxModel.Apply(modelBuilder);
    }

    public override int SaveChanges(bool acceptAllChangesOnSuccess)
    {
        StampTraceContext();
        return base.SaveChanges(acceptAllChangesOnSuccess);
    }

    public override Task<int> SaveChangesAsync(bool acceptAllChangesOnSuccess, CancellationToken cancellationToken = default)
    {
        StampTraceContext();
        return base.SaveChangesAsync(acceptAllChangesOnSuccess, cancellationToken);
    }

    /// <summary>New outbox rows remember the trace they were written in (see <see cref="OutboxMessage.TraceParent"/>).</summary>
    private void StampTraceContext()
    {
        var current = System.Diagnostics.Activity.Current;
        if (current is null || current.IdFormat != System.Diagnostics.ActivityIdFormat.W3C)
            return;
        foreach (var entry in ChangeTracker.Entries<OutboxMessage>())
        {
            if (entry.State == EntityState.Added && entry.Entity.TraceParent is null)
                entry.Entity.TraceParent = current.Id;
        }
    }
}
