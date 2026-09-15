using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Recommendations;

/// <summary>
/// Untrusted recommendation beacon. Occurrence time is never client-supplied. Hidden or forged
/// exposures are accepted without writing so the response cannot probe the catalog.
/// </summary>
public sealed record IngestRecommendationEventRequest(
    Guid EventId,
    RecommendationEventType EventType,
    Guid VisitorId,
    Guid SessionId,
    Guid SourceAssetId,
    Guid TargetAssetId,
    int SlotPosition,
    Guid ExposureId,
    string RankingVersion,
    DateTimeOffset ExpiresAt,
    string ExposureToken,
    IReadOnlyList<Guid>? CandidateIds,
    AnalyticsDeviceClass DeviceClass);
