using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Append-only engagement telemetry row. Does not inherit mutable BaseEntity; there is no update or
/// delete contract, and OccurredAt is the only timestamp. Deliberately carries no navigation properties
/// or foreign keys so ingestion stays a single cheap insert and product removal never cascades here.
/// </summary>
public sealed class AnalyticsEvent
{
    /// <summary>Client-supplied UUID used as the idempotency key for replayed beacons.</summary>
    public Guid Id { get; set; }

    public AnalyticsEventType EventType { get; set; }

    /// <summary>Server-assigned receipt time; client clocks are never trusted.</summary>
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>Owner of the viewed product; every event is scoped to exactly one seller.</summary>
    public Guid SellerId { get; set; }

    /// <summary>Rotating pseudonymous visitor identifier; not tied to an account.</summary>
    public Guid VisitorId { get; set; }

    public Guid SessionId { get; set; }

    /// <summary>Set only when the visitor was authenticated at event time.</summary>
    public Guid? ActorUserId { get; set; }

    public Guid? AssetId { get; set; }
    public Guid? AssetVersionId { get; set; }
    public Guid? BundleId { get; set; }
    public Guid? CollectionId { get; set; }

    public AnalyticsTrafficSource Source { get; set; }

    /// <summary>Normalized bare host; only meaningful for EXTERNAL traffic.</summary>
    public string? ReferrerHost { get; set; }

    public AnalyticsDeviceClass DeviceClass { get; set; }
}
