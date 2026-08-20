namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Seller-level daily engagement rollup keyed by (SellerId, DayUtc). Rebuilt from analytics_events,
/// so counters are derived state rather than an audit trail.
/// </summary>
public sealed class SellerAnalyticsDaily
{
    public Guid SellerId { get; set; }
    public DateOnly DayUtc { get; set; }
    public long AssetViews { get; set; }
    public long BundleViews { get; set; }
    public long CollectionViews { get; set; }
    public long CollectionItemClicks { get; set; }
    public long DownloadRequests { get; set; }
    public long UniqueVisitors { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
