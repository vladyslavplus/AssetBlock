using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class SellerAnalyticsDailyConfiguration : IEntityTypeConfiguration<SellerAnalyticsDaily>
{
    public void Configure(EntityTypeBuilder<SellerAnalyticsDaily> builder)
    {
        builder.ToTable("seller_analytics_daily", table =>
        {
            table.HasCheckConstraint(
                "CK_seller_analytics_daily_counters_non_negative",
                """
                "AssetViews" >= 0 AND "BundleViews" >= 0 AND "CollectionViews" >= 0
                AND "CollectionItemClicks" >= 0 AND "DownloadRequests" >= 0 AND "UniqueVisitors" >= 0
                """);
        });

        builder.HasKey(e => new { e.SellerId, e.DayUtc });

        builder.Property(e => e.AssetViews).IsRequired();
        builder.Property(e => e.BundleViews).IsRequired();
        builder.Property(e => e.CollectionViews).IsRequired();
        builder.Property(e => e.CollectionItemClicks).IsRequired();
        builder.Property(e => e.DownloadRequests).IsRequired();
        builder.Property(e => e.UniqueVisitors).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();
    }
}
