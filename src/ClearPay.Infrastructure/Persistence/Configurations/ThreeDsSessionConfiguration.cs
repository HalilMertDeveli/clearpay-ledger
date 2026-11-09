using ClearPay.Domain.Ledger;
using ClearPay.Domain.Payments;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace ClearPay.Infrastructure.Persistence.Configurations;

internal sealed class ThreeDsSessionConfiguration : IEntityTypeConfiguration<ThreeDsSession>
{
    public void Configure(EntityTypeBuilder<ThreeDsSession> builder)
    {
        builder.ToTable("ThreeDsSession", t =>
            t.HasCheckConstraint("CK_ThreeDsSession_Amount_Positive", "[Amount] > 0"));
        builder.HasKey(x => x.Id);
        builder.Property(x => x.UserId).IsRequired().HasMaxLength(LedgerSchema.UserIdMaxLength);
        builder.Property(x => x.Amount).HasPrecision(LedgerSchema.AmountPrecision, LedgerSchema.AmountScale);
        builder.Property(x => x.Currency).IsRequired().HasMaxLength(3);
        builder.Property(x => x.CardLast4).IsRequired().HasMaxLength(LedgerSchema.LinkedLast4Length);
        builder.Property(x => x.Status).IsRequired();
        builder.Property(x => x.Eci).HasMaxLength(2);
        builder.Property(x => x.AuthenticationValue).HasMaxLength(64);
        builder.Property(x => x.FailureReason).HasMaxLength(200);
        builder.Property(x => x.AcsNonce).HasMaxLength(64);
        builder.Ignore(x => x.IsTerminal);
        builder.HasIndex(x => new { x.Status, x.ExpiresAt }).HasDatabaseName("IX_ThreeDsSession_Status_ExpiresAt");
        builder.HasIndex(x => x.UserId).HasDatabaseName("IX_ThreeDsSession_UserId");
    }
}
