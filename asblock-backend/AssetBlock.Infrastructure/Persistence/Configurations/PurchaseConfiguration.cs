using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class PurchaseConfiguration : IEntityTypeConfiguration<Purchase>
{
    public const string UNIQUE_USER_ASSET = "UIX_purchases_user_asset";
    public const string UNIQUE_ORDER_LINE = "UIX_purchases_order_line";

    public void Configure(EntityTypeBuilder<Purchase> builder)
    {
        builder.ToTable("purchases");

        builder.HasKey(p => p.Id);
        builder.Property(p => p.UserId).IsRequired();
        builder.Property(p => p.AssetId).IsRequired();
        builder.Property(p => p.AssetVersionId).IsRequired();
        builder.Property(p => p.OrderLineId).IsRequired();
        builder.Property(p => p.PurchasedAt).IsRequired();

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
            .HasForeignKey(p => new { p.AssetId, p.AssetVersionId })
            .HasPrincipalKey(v => new { v.AssetId, v.Id })
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(p => p.OrderLine)
            .WithOne(l => l.Purchase)
            .HasForeignKey<Purchase>(p => p.OrderLineId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(p => new { p.UserId, p.AssetId })
            .IsUnique()
            .HasDatabaseName(UNIQUE_USER_ASSET);

        builder.HasIndex(p => p.OrderLineId)
            .IsUnique()
            .HasDatabaseName(UNIQUE_ORDER_LINE);
    }
}
