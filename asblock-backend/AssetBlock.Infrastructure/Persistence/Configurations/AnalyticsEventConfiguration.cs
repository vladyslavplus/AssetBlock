using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class AnalyticsEventConfiguration : IEntityTypeConfiguration<AnalyticsEvent>
{
    public void Configure(EntityTypeBuilder<AnalyticsEvent> builder)
    {
        builder.ToTable("analytics_events", table =>
        {
            table.HasCheckConstraint(
                "CK_analytics_events_EventType",
                $"""
                "EventType" IN (
                    '{nameof(AnalyticsEventType.ASSET_VIEW)}',
                    '{nameof(AnalyticsEventType.BUNDLE_VIEW)}',
                    '{nameof(AnalyticsEventType.COLLECTION_VIEW)}',
                    '{nameof(AnalyticsEventType.COLLECTION_ITEM_CLICK)}',
                    '{nameof(AnalyticsEventType.DOWNLOAD_REQUESTED)}')
                """);

            table.HasCheckConstraint(
                "CK_analytics_events_Source",
                $"""
                "Source" IN (
                    '{nameof(AnalyticsTrafficSource.CATALOG)}',
                    '{nameof(AnalyticsTrafficSource.SEARCH)}',
                    '{nameof(AnalyticsTrafficSource.SELLER_PROFILE)}',
                    '{nameof(AnalyticsTrafficSource.COLLECTION)}',
                    '{nameof(AnalyticsTrafficSource.BUNDLE_PAGE)}',
                    '{nameof(AnalyticsTrafficSource.DIRECT_INTERNAL)}',
                    '{nameof(AnalyticsTrafficSource.EXTERNAL)}',
                    '{nameof(AnalyticsTrafficSource.UNKNOWN)}')
                """);

            table.HasCheckConstraint(
                "CK_analytics_events_DeviceClass",
                $"""
                "DeviceClass" IN (
                    '{nameof(AnalyticsDeviceClass.MOBILE)}',
                    '{nameof(AnalyticsDeviceClass.TABLET)}',
                    '{nameof(AnalyticsDeviceClass.DESKTOP)}',
                    '{nameof(AnalyticsDeviceClass.UNKNOWN)}')
                """);

            // Each event type pins an exact target shape so aggregation never has to guess which
            // columns are meaningful, and a malformed envelope cannot reach storage.
            table.HasCheckConstraint(
                "CK_analytics_events_target_shape",
                $"""
                ("EventType" = '{nameof(AnalyticsEventType.ASSET_VIEW)}'
                    AND "AssetId" IS NOT NULL AND "AssetVersionId" IS NULL AND "BundleId" IS NULL AND "CollectionId" IS NULL)
                OR ("EventType" = '{nameof(AnalyticsEventType.BUNDLE_VIEW)}'
                    AND "BundleId" IS NOT NULL AND "AssetId" IS NULL AND "AssetVersionId" IS NULL AND "CollectionId" IS NULL)
                OR ("EventType" = '{nameof(AnalyticsEventType.COLLECTION_VIEW)}'
                    AND "CollectionId" IS NOT NULL AND "AssetId" IS NULL AND "AssetVersionId" IS NULL AND "BundleId" IS NULL)
                OR ("EventType" = '{nameof(AnalyticsEventType.COLLECTION_ITEM_CLICK)}'
                    AND "CollectionId" IS NOT NULL AND "AssetId" IS NOT NULL AND "AssetVersionId" IS NULL AND "BundleId" IS NULL)
                OR ("EventType" = '{nameof(AnalyticsEventType.DOWNLOAD_REQUESTED)}'
                    AND "AssetId" IS NOT NULL AND "AssetVersionId" IS NOT NULL AND "BundleId" IS NULL AND "CollectionId" IS NULL)
                """);

            table.HasCheckConstraint(
                "CK_analytics_events_ReferrerHost_length",
                $"""
                "ReferrerHost" IS NULL
                OR (length("ReferrerHost") > 0 AND length("ReferrerHost") <= {AnalyticsTelemetryConstants.REFERRER_HOST_MAX_LENGTH})
                """);

            table.HasCheckConstraint(
                "CK_analytics_events_ReferrerHost_source",
                $"""
                "ReferrerHost" IS NULL OR "Source" = '{nameof(AnalyticsTrafficSource.EXTERNAL)}'
                """);
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();

        builder.Property(e => e.EventType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(AnalyticsTelemetryConstants.ENUM_MAX_LENGTH);
        builder.Property(e => e.OccurredAt).IsRequired();
        builder.Property(e => e.SellerId).IsRequired();
        builder.Property(e => e.VisitorId).IsRequired();
        builder.Property(e => e.SessionId).IsRequired();
        builder.Property(e => e.ActorUserId);
        builder.Property(e => e.AssetId);
        builder.Property(e => e.AssetVersionId);
        builder.Property(e => e.BundleId);
        builder.Property(e => e.CollectionId);
        builder.Property(e => e.Source)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(AnalyticsTelemetryConstants.ENUM_MAX_LENGTH);
        builder.Property(e => e.ReferrerHost).HasMaxLength(AnalyticsTelemetryConstants.REFERRER_HOST_MAX_LENGTH);
        builder.Property(e => e.DeviceClass)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(AnalyticsTelemetryConstants.ENUM_MAX_LENGTH);

        builder.HasIndex(e => new { e.SellerId, e.OccurredAt, e.Id })
            .HasDatabaseName("IX_analytics_events_SellerId_OccurredAt_Id");

        builder.HasIndex(e => new { e.SellerId, e.EventType, e.OccurredAt, e.Id })
            .HasDatabaseName("IX_analytics_events_SellerId_EventType_OccurredAt_Id");

        builder.HasIndex(e => new { e.SellerId, e.VisitorId, e.OccurredAt })
            .HasDatabaseName("IX_analytics_events_SellerId_VisitorId_OccurredAt");

        builder.HasIndex(e => new { e.SellerId, e.SessionId, e.OccurredAt })
            .HasDatabaseName("IX_analytics_events_SellerId_SessionId_OccurredAt");

        builder.HasIndex(e => new { e.SellerId, e.AssetId, e.OccurredAt })
            .HasFilter("\"AssetId\" IS NOT NULL")
            .HasDatabaseName("IX_analytics_events_SellerId_AssetId_OccurredAt");

        builder.HasIndex(e => new { e.SellerId, e.BundleId, e.OccurredAt })
            .HasFilter("\"BundleId\" IS NOT NULL")
            .HasDatabaseName("IX_analytics_events_SellerId_BundleId_OccurredAt");

        builder.HasIndex(e => new { e.SellerId, e.CollectionId, e.OccurredAt })
            .HasFilter("\"CollectionId\" IS NOT NULL")
            .HasDatabaseName("IX_analytics_events_SellerId_CollectionId_OccurredAt");
    }
}
