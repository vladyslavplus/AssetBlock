using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class RecommendationDailyConfiguration : IEntityTypeConfiguration<RecommendationDaily>
{
    public void Configure(EntityTypeBuilder<RecommendationDaily> builder)
    {
        builder.ToTable("recommendation_daily", table =>
        {
            table.HasCheckConstraint(
                "CK_recommendation_daily_counters_non_negative",
                """
                "ImpressionCount" >= 0 AND "ClickCount" >= 0
                """);

            table.HasCheckConstraint(
                "CK_recommendation_daily_RankingVersion_length",
                $"""
                length("RankingVersion") > 0
                AND length("RankingVersion") <= {RecommendationTelemetryConstants.RANKING_VERSION_MAX_LENGTH}
                """);
        });

        builder.HasKey(e => new { e.DayUtc, e.SourceAssetId, e.TargetAssetId, e.RankingVersion });
        builder.Property(e => e.RankingVersion)
            .IsRequired()
            .HasMaxLength(RecommendationTelemetryConstants.RANKING_VERSION_MAX_LENGTH);
        builder.Property(e => e.ImpressionCount).IsRequired();
        builder.Property(e => e.ClickCount).IsRequired();
        builder.Property(e => e.UpdatedAt).IsRequired();

        // Popularity ranking: source-scoped engagement lookup by (source, targets) over a
        // trailing UTC-day window. Equality predicates precede the DayUtc range.
        builder.HasIndex(e => new { e.SourceAssetId, e.TargetAssetId, e.DayUtc })
            .HasDatabaseName("IX_recommendation_daily_source_target_day");
    }
}
