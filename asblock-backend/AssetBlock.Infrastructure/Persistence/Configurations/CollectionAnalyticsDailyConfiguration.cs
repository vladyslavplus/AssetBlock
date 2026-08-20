using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class CollectionAnalyticsDailyConfiguration : IEntityTypeConfiguration<CollectionAnalyticsDaily>
{
    public void Configure(EntityTypeBuilder<CollectionAnalyticsDaily> builder)
    {
        builder.ToTable("collection_analytics_daily", table =>
        {
            table.HasCheckConstraint(
                "CK_collection_analytics_daily_counters_non_negative",
                """
                "Views" >= 0 AND "ItemClicks" >= 0 AND "UniqueVisitors" >= 0
                """);
        });

        builder.HasKey(e => new { e.SellerId, e.DayUtc, e.CollectionId });

        builder.Property(e => e.Views).IsRequired();
        builder.Property(e => e.ItemClicks).IsRequired();
        builder.Property(e => e.UniqueVisitors).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();
    }
}
