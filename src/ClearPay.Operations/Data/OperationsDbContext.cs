using ClearPay.Domain.Reconciliation;
using ClearPay.Messaging;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace ClearPay.Operations.Data;

/// <summary>
/// The Operations service's own database (schema <c>ops</c>). Nothing here is shared with the ledger:
/// payment operations arrive as events and are kept as a projection.
/// </summary>
public sealed class OperationsDbContext : DbContext
{
    public const string Schema = "ops";

    public OperationsDbContext(DbContextOptions<OperationsDbContext> options)
        : base(options)
    {
    }

    public DbSet<PaymentOperationProjection> PaymentOperations => Set<PaymentOperationProjection>();

    public DbSet<Notification> Notifications => Set<Notification>();

    public DbSet<ReconciliationRun> ReconciliationRuns => Set<ReconciliationRun>();

    public DbSet<ReconciliationItem> ReconciliationItems => Set<ReconciliationItem>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema(Schema);
        InboxModel.Apply(modelBuilder, Schema);

        modelBuilder.Entity<PaymentOperationProjection>(b =>
        {
            b.ToTable("PaymentOperation");
            b.HasKey(x => x.OperationId);
            b.Property(x => x.OperationId).ValueGeneratedNever();
            b.Property(x => x.UserId).IsRequired().HasMaxLength(450);
            b.Property(x => x.Type).IsRequired().HasMaxLength(16);
            b.Property(x => x.Status).IsRequired().HasMaxLength(16);
            b.Property(x => x.Provider).IsRequired().HasMaxLength(32);
            b.Property(x => x.ProviderReference).HasMaxLength(64);
            b.Property(x => x.Amount).HasPrecision(18, 2);
            // Nightly reconciliation reads one day of captures/refunds (ops.usp_SettlementCandidates).
            b.HasIndex(x => new { x.CreatedAt, x.Type })
                .HasDatabaseName("IX_PaymentOperation_CreatedAt_Type")
                .IncludeProperties(x => new { x.Status, x.Provider, x.ProviderReference, x.MerchantReference, x.Amount });
        });

        modelBuilder.Entity<Notification>(b =>
        {
            b.ToTable("Notification");
            b.HasKey(x => x.Id);
            b.Property(x => x.Recipient).IsRequired().HasMaxLength(450);
            b.Property(x => x.Channel).IsRequired().HasMaxLength(32);
            b.Property(x => x.Template).IsRequired().HasMaxLength(64);
            b.Property(x => x.Text).IsRequired().HasMaxLength(500);
            b.HasIndex(x => new { x.Recipient, x.CreatedAt }).HasDatabaseName("IX_Notification_Recipient_CreatedAt");
            b.HasIndex(x => x.SourceMessageId).IsUnique().HasDatabaseName("UX_Notification_SourceMessageId");
        });

        modelBuilder.Entity<ReconciliationRun>(b =>
        {
            b.ToTable("ReconciliationRun");
            b.HasKey(x => x.Id);
            b.Property(x => x.Status).IsRequired().HasMaxLength(16);
            b.Property(x => x.LedgerTotal).HasPrecision(18, 2);
            b.Property(x => x.SettlementTotal).HasPrecision(18, 2);
            b.Property(x => x.Error).HasMaxLength(2000);
            b.HasIndex(x => new { x.BusinessDate, x.Status }).HasDatabaseName("IX_ReconciliationRun_BusinessDate_Status");
        });

        modelBuilder.Entity<ReconciliationItem>(b =>
        {
            b.ToTable("ReconciliationItem");
            b.HasKey(x => x.Id);
            b.Property(x => x.Kind).IsRequired().HasMaxLength(40);
            b.Property(x => x.Provider).IsRequired().HasMaxLength(32);
            b.Property(x => x.Type).IsRequired().HasMaxLength(16);
            b.Property(x => x.Reference).HasMaxLength(64);
            b.Property(x => x.LedgerAmount).HasPrecision(18, 2);
            b.Property(x => x.SettlementAmount).HasPrecision(18, 2);
            b.Property(x => x.Detail).HasMaxLength(400);
            b.HasOne<ReconciliationRun>().WithMany().HasForeignKey(x => x.RunId).OnDelete(DeleteBehavior.Cascade);
            b.HasIndex(x => new { x.RunId, x.Kind }).HasDatabaseName("IX_ReconciliationItem_RunId_Kind");
        });
    }
}

/// <summary>Last known state of one PSP operation, built from <c>clearpay.payment.operation.recorded</c> events.</summary>
public sealed class PaymentOperationProjection
{
    public Guid OperationId { get; set; }

    public Guid PaymentId { get; set; }

    public string UserId { get; set; } = string.Empty;

    public string Type { get; set; } = string.Empty;

    public string Status { get; set; } = string.Empty;

    public string Provider { get; set; } = string.Empty;

    public string? ProviderReference { get; set; }

    public Guid MerchantReference { get; set; }

    public decimal Amount { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>A message the demo "mailbox" received. Nothing is really sent: no e-mail, SMS or push provider.</summary>
public sealed class Notification
{
    public Guid Id { get; set; }

    /// <summary>The event that caused it; unique, so a redelivered event can never notify twice.</summary>
    public Guid SourceMessageId { get; set; }

    public string Recipient { get; set; } = string.Empty;

    public string Channel { get; set; } = "demo-mailbox";

    public string Template { get; set; } = string.Empty;

    public string Text { get; set; } = string.Empty;

    public Guid CorrelationId { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>dotnet-ef design-time factory (migrations only).</summary>
public sealed class OperationsDbContextFactory : IDesignTimeDbContextFactory<OperationsDbContext>
{
    public OperationsDbContext CreateDbContext(string[] args) =>
        new(new DbContextOptionsBuilder<OperationsDbContext>()
            .UseSqlServer("Server=(localdb)\\MSSQLLocalDB;Database=ClearPayOps;Integrated Security=True;TrustServerCertificate=True;Encrypt=True")
            .Options);
}
