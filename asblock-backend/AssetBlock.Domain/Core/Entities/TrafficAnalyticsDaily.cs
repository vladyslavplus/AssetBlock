using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Daily traffic-source rollup keyed by (SellerId, DayUtc, Source, ReferrerHostKey).
/// ReferrerHostKey is the empty string for non-external sources and for external traffic without a
/// usable host, keeping the composite key non-nullable.
/// </summary>
public sealed class TrafficAnalyticsDaily
{
    public Guid SellerId { get; set; }
    public DateOnly DayUtc { get; set; }
    public AnalyticsTrafficSource Source { get; set; }
    public string ReferrerHostKey { get; set; } = string.Empty;
    public long ProductViews { get; set; }
    public long UniqueVisitors { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
