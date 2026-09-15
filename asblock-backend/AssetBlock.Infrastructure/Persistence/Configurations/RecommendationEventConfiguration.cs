using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace AssetBlock.Infrastructure.Persistence.Configurations;

internal sealed class RecommendationEventConfiguration : IEntityTypeConfiguration<RecommendationEvent>
{
    private const string UNIQUE_EXPOSURE_EVENT_TARGET =
        "UX_recommendation_events_ExposureId_EventType_TargetAssetId";

    public void Configure(EntityTypeBuilder<RecommendationEvent> builder)
    {
        builder.ToTable("recommendation_events", table =>
        {
            table.HasCheckConstraint(
                "CK_recommendation_events_EventType",
                $"""
                "EventType" IN (
                    '{nameof(RecommendationEventType.IMPRESSION)}',
                    '{nameof(RecommendationEventType.CLICK)}')
                """);

            table.HasCheckConstraint(
                "CK_recommendation_events_DeviceClass",
                $"""
                "DeviceClass" IN (
                    '{nameof(AnalyticsDeviceClass.MOBILE)}',
                    '{nameof(AnalyticsDeviceClass.TABLET)}',
                    '{nameof(AnalyticsDeviceClass.DESKTOP)}',
                    '{nameof(AnalyticsDeviceClass.UNKNOWN)}')
                """);

            table.HasCheckConstraint(
                "CK_recommendation_events_SlotPosition",
                $"""
                "SlotPosition" >= {RecommendationTelemetryConstants.SLOT_MIN}
                AND "SlotPosition" <= {RecommendationTelemetryConstants.SLOT_MAX}
                """);

            table.HasCheckConstraint(
                "CK_recommendation_events_RankingVersion_length",
                $"""
                length("RankingVersion") > 0
                AND length("RankingVersion") <= {RecommendationTelemetryConstants.RANKING_VERSION_MAX_LENGTH}
                """);
        });

        builder.HasKey(e => e.Id);
        builder.Property(e => e.Id).ValueGeneratedNever();
        builder.Property(e => e.EventType)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(AnalyticsTelemetryConstants.ENUM_MAX_LENGTH);
        builder.Property(e => e.OccurredAt).IsRequired();
        builder.Property(e => e.ExposureId).IsRequired();
        builder.Property(e => e.SourceAssetId).IsRequired();
        builder.Property(e => e.TargetAssetId).IsRequired();
        builder.Property(e => e.SlotPosition).IsRequired();
        builder.Property(e => e.RankingVersion)
            .IsRequired()
            .HasMaxLength(RecommendationTelemetryConstants.RANKING_VERSION_MAX_LENGTH);
        builder.Property(e => e.VisitorId).IsRequired();
        builder.Property(e => e.SessionId).IsRequired();
        builder.Property(e => e.ActorUserId);
        builder.Property(e => e.DeviceClass)
            .IsRequired()
            .HasConversion<string>()
            .HasMaxLength(AnalyticsTelemetryConstants.ENUM_MAX_LENGTH);

        builder.HasIndex(e => new { e.ExposureId, e.EventType, e.TargetAssetId })
            .IsUnique()
            .HasDatabaseName(UNIQUE_EXPOSURE_EVENT_TARGET);

        builder.HasIndex(e => new { e.OccurredAt, e.Id })
            .HasDatabaseName("IX_recommendation_events_OccurredAt_Id");

        // Worker recompute sources one account's clicks in a rolling window.
        // Equality-equality-range order matches that predicate; without it the per-user
        // recompute scans the raw table.
        builder.HasIndex(e => new { e.ActorUserId, e.EventType, e.OccurredAt })
            .HasDatabaseName("IX_recommendation_events_ActorUserId_EventType_OccurredAt");
    }
}
