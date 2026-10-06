using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class PaidCheckoutReconciliationHoldConfiguration : IEntityTypeConfiguration<PaidCheckoutReconciliationHold>
{
    public void Configure(EntityTypeBuilder<PaidCheckoutReconciliationHold> builder)
    {
        builder.ToTable("paid_checkout_reconciliation_holds");
        builder.HasKey(h => h.Id);
        builder.Property(h => h.State).HasConversion<string>().HasMaxLength(32).IsRequired();
        builder.Property(h => h.SafePaymentFactsJson).HasColumnType("jsonb").IsRequired();
        builder.Property(h => h.ItemIdentitiesJson).HasColumnType("jsonb").IsRequired();

        builder.HasOne(h => h.CheckoutIntent).WithMany().HasForeignKey(h => h.CheckoutIntentId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(h => h.CheckoutIntentId).IsUnique();
        builder.HasIndex(h => h.StripeEventId).IsUnique().HasFilter("\"StripeEventId\" IS NOT NULL");
    }
}
