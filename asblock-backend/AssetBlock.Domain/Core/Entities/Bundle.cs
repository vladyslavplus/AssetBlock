using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Sellable single-seller multi-asset offer. Product definition lives on immutable revisions.
/// </summary>
public class Bundle : BaseEntity
{
    public required Guid SellerId { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }

    public User Seller { get; set; } = null!;
    public ICollection<BundleRevision> Revisions { get; set; } = new List<BundleRevision>();
}
