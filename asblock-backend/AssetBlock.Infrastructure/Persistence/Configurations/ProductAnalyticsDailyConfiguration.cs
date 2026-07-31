using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class ProductAnalyticsDailyConfiguration : IEntityTypeConfiguration<ProductAnalyticsDaily>
{
    public void Configure(EntityTypeBuilder<ProductAnalyticsDaily> builder)
    {
        builder.ToTable("product_analytics_daily", table =>
        {
            table.HasCheckConstraint(
                "CK_product_analytics_daily_ProductType",
                $"""
                "ProductType" IN ('{nameof(AnalyticsProductKind.ASSET)}', '{nameof(AnalyticsProductKind.BUNDLE)}')
                """);

            table.HasCheckConstraint(
                "CK_product_analytics_daily_counters_non_negative",
                """
                "Views" >= 0 AND "DownloadRequests" >= 0 AND "UniqueVisitors" >= 0
                """);
        });

        builder.HasKey(e => new { e.SellerId, e.DayUtc, e.ProductType, e.ProductId });

        builder.Property(e => e.ProductType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(AnalyticsTelemetryConstants.ENUM_MAX_LENGTH);
        builder.Property(e => e.Views).IsRequired();
        builder.Property(e => e.DownloadRequests).IsRequired();
        builder.Property(e => e.UniqueVisitors).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();
    }
}
