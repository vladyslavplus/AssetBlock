namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Immutable membership snapshot for a bundle revision.
/// AssetId is nullable so hard-deleted assets keep historical title/price snapshots.
/// </summary>
public class BundleRevisionItem
{
    public required Guid Id { get; init; }
    public required Guid BundleRevisionId { get; set; }

    /// <summary>Null after hard asset delete (ON DELETE SET NULL).</summary>
    public Guid? AssetId { get; set; }

    /// <summary>One-based position within the revision.</summary>
    public required int Position { get; set; }

    public required string AssetTitleSnapshot { get; set; }
    public required decimal ListPriceSnapshot { get; set; }

    public BundleRevision BundleRevision { get; set; } = null!;
    public Asset? Asset { get; set; }
}
