namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Per-collection daily engagement rollup keyed by (SellerId, DayUtc, CollectionId).
/// No foreign key to collections so deleting a collection cannot erase historical counts.
/// </summary>
public sealed class CollectionAnalyticsDaily
{
    public Guid SellerId { get; set; }
    public DateOnly DayUtc { get; set; }
    public Guid CollectionId { get; set; }
    public long Views { get; set; }
    public long ItemClicks { get; set; }
    public long UniqueVisitors { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}
