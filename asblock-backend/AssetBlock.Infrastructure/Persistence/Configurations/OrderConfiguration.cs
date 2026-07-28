using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class OrderConfiguration : IEntityTypeConfiguration<Order>
{
    public void Configure(EntityTypeBuilder<Order> builder)
    {
        builder.ToTable("orders", table =>
        {
            table.HasCheckConstraint("CK_orders_amount_paid_positive", "\"AmountPaid\" > 0");
            table.HasCheckConstraint(
                "CK_orders_currency_iso_lower",
                "length(\"Currency\") = 3 AND \"Currency\" = lower(\"Currency\")");
            table.HasCheckConstraint(
                "CK_orders_currency_usd_v1",
                "\"Currency\" = 'usd'");
            table.HasCheckConstraint(
                "CK_orders_exactly_one_product",
                """
                ("AssetId" IS NOT NULL AND "BundleId" IS NULL AND "BundleRevisionId" IS NULL)
                OR ("AssetId" IS NULL AND "BundleId" IS NOT NULL AND "BundleRevisionId" IS NOT NULL)
                """);
        });

        builder.HasKey(o => o.Id);
        builder.Property(o => o.UserId).IsRequired();
        builder.Property(o => o.CheckoutIntentId).IsRequired();
        builder.Property(o => o.AssetId);
        builder.Property(o => o.BundleId);
        builder.Property(o => o.BundleRevisionId);
        builder.Property(o => o.ProductTitle).IsRequired().HasMaxLength(500);
        builder.Property(o => o.StripeSessionId).IsRequired().HasMaxLength(256);
        builder.Property(o => o.AmountPaid).IsRequired().HasPrecision(18, 2);
        builder.Property(o => o.Currency).IsRequired().HasMaxLength(3);
        builder.Property(o => o.PurchasedAt).IsRequired();
        builder.Property(o => o.CreatedAt).IsRequired();

        builder.HasOne(o => o.User)
            .WithMany()
            .HasForeignKey(o => o.UserId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.CheckoutIntent)
            .WithOne(i => i.Order)
            .HasForeignKey<Order>(o => o.CheckoutIntentId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.Asset)
            .WithMany()
            .HasForeignKey(o => o.AssetId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.Bundle)
            .WithMany()
            .HasForeignKey(o => o.BundleId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne(o => o.BundleRevision)
            .WithMany()
            .HasForeignKey(o => o.BundleRevisionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(o => o.CheckoutIntentId)
            .IsUnique()
            .HasDatabaseName("UIX_orders_checkout_intent");

        builder.HasIndex(o => o.StripeSessionId)
            .IsUnique()
            .HasDatabaseName("UIX_orders_stripe_session");

        builder.HasIndex(o => new { o.UserId, o.PurchasedAt, o.Id })
            .HasDatabaseName("IX_orders_user_purchased");

        builder.HasIndex(o => new { o.PurchasedAt, o.Id })
            .HasDatabaseName("IX_orders_purchased_id");

        builder.HasIndex(o => new { o.BundleId, o.PurchasedAt, o.Id })
            .HasFilter("\"BundleId\" IS NOT NULL")
            .HasDatabaseName("IX_orders_bundle_purchased_id");
    }
}
