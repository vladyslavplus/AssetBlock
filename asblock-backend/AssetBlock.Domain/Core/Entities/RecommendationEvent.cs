using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Append-only recommendation impression or click. No foreign keys, no IP, URLs, query text, or vectors.
/// ActorUserId is stored only when the visitor was authenticated at ingest time and is never backfilled.
/// </summary>
public sealed class RecommendationEvent
{
    public Guid Id { get; set; }

    public RecommendationEventType EventType { get; set; }

    /// <summary>Server-assigned receipt time; client clocks are never trusted.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    public Guid ExposureId { get; set; }

    public Guid SourceAssetId { get; set; }

    public Guid TargetAssetId { get; set; }

    public int SlotPosition { get; set; }

    public string RankingVersion { get; set; } = string.Empty;

    public Guid VisitorId { get; set; }

    public Guid SessionId { get; set; }

    public Guid? ActorUserId { get; set; }

    public AnalyticsDeviceClass DeviceClass { get; set; }
}
