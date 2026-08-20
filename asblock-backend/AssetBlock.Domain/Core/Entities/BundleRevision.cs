using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Append-only immutable snapshot of a bundle offer. Only IsCurrent may change after insert.
/// </summary>
public class BundleRevision : BaseEntity
{
    public required Guid BundleId { get; set; }
    public required int RevisionNumber { get; set; }
    public required bool IsCurrent { get; set; }
    public required string Title { get; set; }
    public string? Description { get; set; }
    public required decimal Price { get; set; }
    public required string Currency { get; set; }
    public required decimal ListPriceTotal { get; set; }

    public Bundle Bundle { get; set; } = null!;
    public ICollection<BundleRevisionItem> Items { get; set; } = new List<BundleRevisionItem>();
}
