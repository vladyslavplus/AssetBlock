using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class PurchaseConfiguration : IEntityTypeConfiguration<Purchase>
{
    public void Configure(EntityTypeBuilder<Purchase> builder)
    {
        builder.ToTable("purchases");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.UserId).IsRequired();
        builder.Property(p => p.AssetId).IsRequired();
        builder.Property(p => p.AssetVersionId).IsRequired();
        builder.Property(p => p.CheckoutIntentId).IsRequired();
        builder.Property(p => p.StripePaymentId).IsRequired().HasMaxLength(256);
        builder.Property(p => p.PricePaid).IsRequired().HasPrecision(18, 2);
        builder.Property(p => p.Currency).IsRequired().HasMaxLength(3);
        builder.Property(p => p.PurchasedAt).IsRequired();

        builder.HasIndex(p => p.StripePaymentId).IsUnique();

        builder.HasOne(p => p.User)
            .WithMany(u => u.Purchases)
            .HasForeignKey(p => p.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.Asset)
            .WithMany(a => a.Purchases)
            .HasForeignKey(p => p.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.AssetVersion)
            .WithMany(v => v.Purchases)
            .HasForeignKey(p => p.AssetVersionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.CheckoutIntent)
            .WithOne(i => i.Purchase)
            .HasForeignKey<Purchase>(p => p.CheckoutIntentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.UserId, p.AssetId }).IsUnique();
        builder.HasIndex(p => p.CheckoutIntentId).IsUnique();
    }
}
