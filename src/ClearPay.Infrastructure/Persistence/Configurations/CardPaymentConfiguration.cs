using ClearPay.Domain.Ledger;
using ClearPay.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClearPay.Infrastructure.Persistence.Configurations;

internal sealed class CardPaymentConfiguration : IEntityTypeConfiguration<CardPayment>
{
    public void Configure(EntityTypeBuilder<CardPayment> builder)
    {
        // "* 1.0" keeps the comparisons numeric on SQLite, which stores decimals as TEXT (tests).
        builder.ToTable("CardPayment", t =>
        {
            t.HasCheckConstraint("CK_CardPayment_Amount_Positive", "[Amount] > 0");
            t.HasCheckConstraint("CK_CardPayment_Refund_LE_Captured", "[RefundedAmount] * 1.0 >= 0 AND [RefundedAmount] * 1.0 <= [CapturedAmount] * 1.0");
            t.HasCheckConstraint("CK_CardPayment_Captured_LE_Amount", "[CapturedAmount] * 1.0 >= 0 AND [CapturedAmount] * 1.0 <= [Amount] * 1.0");
        });
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserId).IsRequired().HasMaxLength(LedgerSchema.UserIdMaxLength);
        builder.Property(x => x.Provider).IsRequired().HasMaxLength(32);
        builder.Property(x => x.ProviderReference).HasMaxLength(64);
        builder.Property(x => x.Amount).HasPrecision(LedgerSchema.AmountPrecision, LedgerSchema.AmountScale);
        builder.Property(x => x.CapturedAmount).HasPrecision(LedgerSchema.AmountPrecision, LedgerSchema.AmountScale);
        builder.Property(x => x.RefundedAmount).HasPrecision(LedgerSchema.AmountPrecision, LedgerSchema.AmountScale);
        builder.Property(x => x.CardLast4).IsRequired().HasMaxLength(LedgerSchema.LinkedLast4Length);
        builder.Property(x => x.DeclineCode).HasMaxLength(32);
        builder.Ignore(x => x.RefundableAmount);
        builder.Ignore(x => x.CanCapture);
        builder.Ignore(x => x.CanVoid);
        builder.HasIndex(x => x.ThreeDsSessionId).IsUnique().HasDatabaseName("UX_CardPayment_ThreeDsSessionId");
        builder.HasIndex(x => x.UserId).HasDatabaseName("IX_CardPayment_UserId");
    }
}

internal sealed class PaymentOperationConfiguration : IEntityTypeConfiguration<PaymentOperation>
{
    public void Configure(EntityTypeBuilder<PaymentOperation> builder)
    {
        builder.ToTable("PaymentOperation", t =>
            t.HasCheckConstraint("CK_PaymentOperation_Amount_Positive", "[Amount] > 0"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Amount).HasPrecision(LedgerSchema.AmountPrecision, LedgerSchema.AmountScale);
        builder.Property(x => x.Provider).IsRequired().HasMaxLength(32);
        builder.Property(x => x.ProviderReference).HasMaxLength(64);
        builder.Property(x => x.ProviderIdempotencyKey).IsRequired().HasMaxLength(80);
        builder.HasOne<CardPayment>().WithMany().HasForeignKey(x => x.PaymentId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(x => new { x.Provider, x.ProviderIdempotencyKey }).IsUnique()
            .HasDatabaseName("UX_PaymentOperation_Provider_Key");
        builder.HasIndex(x => x.PaymentId).HasDatabaseName("IX_PaymentOperation_PaymentId");
    }
}

internal sealed class SimPspTransactionConfiguration : IEntityTypeConfiguration<SimPspTransaction>
{
    public void Configure(EntityTypeBuilder<SimPspTransaction> builder)
    {
        // "sim" schema: the fake PSPs' own storage, never read by the payment service.
        builder.ToTable("PspTransaction", "sim");
        builder.HasKey(x => x.Id);
        builder.Property(x => x.Provider).IsRequired().HasMaxLength(32);
        builder.Property(x => x.IdempotencyKey).IsRequired().HasMaxLength(80);
        builder.Property(x => x.AuthorizationReference).HasMaxLength(64);
        builder.Property(x => x.Reference).IsRequired().HasMaxLength(64);
        builder.Property(x => x.Amount).HasPrecision(LedgerSchema.AmountPrecision, LedgerSchema.AmountScale);
        builder.Property(x => x.DeclineCode).HasMaxLength(32);
        builder.HasIndex(x => new { x.Provider, x.IdempotencyKey }).IsUnique().HasDatabaseName("UX_SimPspTransaction_Provider_Key");
        builder.HasIndex(x => new { x.Provider, x.Reference }).HasDatabaseName("IX_SimPspTransaction_Provider_Reference");
    }
}
