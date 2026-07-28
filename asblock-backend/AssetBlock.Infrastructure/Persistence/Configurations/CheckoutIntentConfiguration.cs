using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CheckoutIntentConfiguration : IEntityTypeConfiguration<CheckoutIntent>
{
    public void Configure(EntityTypeBuilder<CheckoutIntent> builder)
    {
        builder.ToTable("checkout_intents", table =>
        {
            table.HasCheckConstraint("CK_checkout_intents_amount_total_positive", "\"AmountTotal\" > 0");
            table.HasCheckConstraint("CK_checkout_intents_expires_after_created", "\"ExpiresAt\" > \"CreatedAt\"");
            table.HasCheckConstraint(
                "CK_checkout_intents_currency_iso_lower",
                "\"Currency\" ~ '^[a-z]{3}$'");
            table.HasCheckConstraint(
                "CK_checkout_intents_currency_usd_v1",
                "\"Currency\" = 'usd'");
            table.HasCheckConstraint(
                "CK_checkout_intents_exactly_one_product",
                """
                ("AssetId" IS NOT NULL AND "BundleId" IS NULL AND "BundleRevisionId" IS NULL)
                OR ("AssetId" IS NULL AND "BundleId" IS NOT NULL AND "BundleRevisionId" IS NOT NULL)
                """);
        });

        builder.HasKey(i => i.Id);
        builder.Property(i => i.UserId).IsRequired();
        builder.Property(i => i.AssetId);
        builder.Property(i => i.BundleId);
        builder.Property(i => i.BundleRevisionId);
        builder.Property(i => i.ProductTitle).IsRequired().HasMaxLength(500);
        builder.Property(i => i.AmountTotal).IsRequired().HasPrecision(18, 2);
        builder.Property(i => i.Currency).IsRequired().HasMaxLength(3);
        builder.Property(i => i.StripeSessionId).HasMaxLength(256);
        builder.Property(i => i.Status)
            .IsRequired()
            .HasMaxLength(16)
            .HasConversion(
                status => status.ToString(),
                raw => Enum.Parse<CheckoutIntentStatus>(raw));
        builder.Property(i => i.ExpiresAt).IsRequired();
        builder.Property(i => i.CompletedAt);
        builder.Property(i => i.LastStripeReconciledAt);
        builder.Property(i => i.CreatedAt).IsRequired();

        builder.HasOne(i => i.User)
            .WithMany()
            .HasForeignKey(i => i.UserId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.Asset)
            .WithMany()
            .HasForeignKey(i => i.AssetId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.Bundle)
            .WithMany()
            .HasForeignKey(i => i.BundleId)
            .OnDelete(DeleteBehavior.Restrict);
        builder.HasOne(i => i.BundleRevision)
            .WithMany()
            .HasForeignKey(i => i.BundleRevisionId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(i => i.StripeSessionId)
            .IsUnique()
            .HasDatabaseName("UIX_checkout_intents_stripe_session");

        builder.HasIndex(i => new { i.UserId, i.AssetId })
            .IsUnique()
            .HasFilter("\"Status\" = 'PENDING' AND \"AssetId\" IS NOT NULL")
            .HasDatabaseName("UIX_checkout_intents_user_asset_pending");

        builder.HasIndex(i => new { i.UserId, i.BundleId })
            .IsUnique()
            .HasFilter("\"Status\" = 'PENDING' AND \"BundleId\" IS NOT NULL")
            .HasDatabaseName("UIX_checkout_intents_user_bundle_pending");

        builder.HasIndex(i => new { i.Status, i.ExpiresAt, i.Id })
            .HasDatabaseName("IX_checkout_intents_status_expires");

        builder.HasIndex(i => new { i.Status, i.CreatedAt, i.Id })
            .HasFilter("\"Status\" = 'PENDING' AND \"StripeSessionId\" IS NOT NULL")
            .HasDatabaseName("IX_checkout_intents_pending_attached_reconcile");
    }
}
