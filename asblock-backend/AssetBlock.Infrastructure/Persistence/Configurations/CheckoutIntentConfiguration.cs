using AssetBlock.Domain.Core.Constants;
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
                // Portable across PostgreSQL and SQLite unit-test EnsureCreated (no PG '~' regex).
                "length(\"Currency\") = 3 AND \"Currency\" = lower(\"Currency\")");
            table.HasCheckConstraint(
                "CK_checkout_intents_currency_usd_v1",
                "\"Currency\" = 'usd'");
            table.HasCheckConstraint(
                "CK_checkout_intents_exactly_one_product",
                """
                ("AssetId" IS NOT NULL AND "BundleId" IS NULL AND "BundleRevisionId" IS NULL)
                OR ("AssetId" IS NULL AND "BundleId" IS NOT NULL AND "BundleRevisionId" IS NOT NULL)
                """);
            table.HasCheckConstraint(
                "CK_checkout_intents_AttributionSource",
                $"""
                "AttributionSource" IS NULL OR "AttributionSource" IN (
                    '{nameof(AnalyticsTrafficSource.CATALOG)}',
                    '{nameof(AnalyticsTrafficSource.SEARCH)}',
                    '{nameof(AnalyticsTrafficSource.SELLER_PROFILE)}',
                    '{nameof(AnalyticsTrafficSource.COLLECTION)}',
                    '{nameof(AnalyticsTrafficSource.BUNDLE_PAGE)}',
                    '{nameof(AnalyticsTrafficSource.DIRECT_INTERNAL)}',
                    '{nameof(AnalyticsTrafficSource.EXTERNAL)}',
                    '{nameof(AnalyticsTrafficSource.UNKNOWN)}')
                """);
            // Collection attribution is only meaningful for a single-asset purchase reached from a
            // collection, so the collection id and the COLLECTION source must always travel together.
            table.HasCheckConstraint(
                "CK_checkout_intents_attribution_collection",
                $"""
                ("AttributionSource" = '{nameof(AnalyticsTrafficSource.COLLECTION)}'
                    AND "AttributionCollectionId" IS NOT NULL
                    AND "AssetId" IS NOT NULL
                    AND "BundleId" IS NULL)
                OR ("AttributionSource" IS DISTINCT FROM '{nameof(AnalyticsTrafficSource.COLLECTION)}'
                    AND "AttributionCollectionId" IS NULL)
                """);
            table.HasCheckConstraint(
                "CK_checkout_intents_attribution_null_consistency",
                """
                "AttributionSource" IS NOT NULL
                OR ("AnalyticsVisitorId" IS NULL
                    AND "AnalyticsSessionId" IS NULL
                    AND "AttributionCollectionId" IS NULL
                    AND "AttributionReferrerHost" IS NULL)
                """);
            table.HasCheckConstraint(
                "CK_checkout_intents_attribution_referrer_host",
                $"""
                "AttributionReferrerHost" IS NULL
                OR "AttributionSource" = '{nameof(AnalyticsTrafficSource.EXTERNAL)}'
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

        builder.Property(i => i.AnalyticsVisitorId);
        builder.Property(i => i.AnalyticsSessionId);
        builder.Property(i => i.AttributionSource)
            .HasMaxLength(AnalyticsTelemetryConstants.ENUM_MAX_LENGTH)
            .HasConversion<string>();
        // No foreign key: attribution must survive deletion of the referring collection.
        builder.Property(i => i.AttributionCollectionId);
        builder.Property(i => i.AttributionReferrerHost)
            .HasMaxLength(AnalyticsTelemetryConstants.REFERRER_HOST_MAX_LENGTH);

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
