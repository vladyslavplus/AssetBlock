using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Per-product daily engagement rollup keyed by (SellerId, DayUtc, ProductType, ProductId).
/// ProductId points at an asset or a bundle depending on ProductType; there is no foreign key so
/// deleting a product cannot erase historical counts.
/// </summary>
public sealed class ProductAnalyticsDaily
{
    public Guid SellerId { get; set; }
    public DateOnly DayUtc { get; set; }
    public AnalyticsProductKind ProductType { get; set; }
    public Guid ProductId { get; set; }
    public long Views { get; set; }
    public long DownloadRequests { get; set; }
    public long UniqueVisitors { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
