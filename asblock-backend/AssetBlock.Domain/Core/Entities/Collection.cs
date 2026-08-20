using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Seller-curated editorial grouping of assets. No price, checkout, entitlement, or license.
/// </summary>
public class Collection : BaseEntity
{
    public required Guid SellerId { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public required CollectionStatus Status { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public User Seller { get; set; } = null!;
    public ICollection<CollectionItem> Items { get; set; } = new List<CollectionItem>();
}
