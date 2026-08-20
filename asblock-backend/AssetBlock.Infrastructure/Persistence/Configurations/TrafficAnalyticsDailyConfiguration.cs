using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class TrafficAnalyticsDailyConfiguration : IEntityTypeConfiguration<TrafficAnalyticsDaily>
{
    public void Configure(EntityTypeBuilder<TrafficAnalyticsDaily> builder)
    {
        builder.ToTable("traffic_analytics_daily", table =>
        {
            table.HasCheckConstraint(
                "CK_traffic_analytics_daily_Source",
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

            // Only external traffic can carry a host; every other source uses the empty sentinel key.
            table.HasCheckConstraint(
                "CK_traffic_analytics_daily_ReferrerHostKey_external_only",
                $"""
                "ReferrerHostKey" = '' OR "Source" = '{nameof(AnalyticsTrafficSource.EXTERNAL)}'
                """);

            table.HasCheckConstraint(
                "CK_traffic_analytics_daily_counters_non_negative",
                """
                "ProductViews" >= 0 AND "UniqueVisitors" >= 0
                """);
        });

        builder.HasKey(e => new { e.SellerId, e.DayUtc, e.Source, e.ReferrerHostKey });

        builder.Property(e => e.Source)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(AnalyticsTelemetryConstants.ENUM_MAX_LENGTH);
        builder.Property(e => e.ReferrerHostKey)
            .IsRequired()
            .HasMaxLength(AnalyticsTelemetryConstants.REFERRER_HOST_MAX_LENGTH);
        builder.Property(e => e.ProductViews).IsRequired();
        builder.Property(e => e.UniqueVisitors).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();
    }
}
