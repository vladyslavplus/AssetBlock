using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Untrusted engagement beacon envelope. Target ids must match the shape required by the event type;
/// the referrer is accepted raw and normalized server-side, and OccurredAt is never client-supplied.
/// </summary>
public sealed record IngestAnalyticsEventRequest(
    Guid EventId,
    AnalyticsEventType EventType,
    Guid VisitorId,
    Guid SessionId,
    Guid? AssetId,
    Guid? AssetVersionId,
    Guid? BundleId,
    Guid? CollectionId,
    AnalyticsTrafficSource Source,
    string? ReferrerHost,
    AnalyticsDeviceClass DeviceClass);
