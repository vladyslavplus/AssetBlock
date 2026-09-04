namespace AssetBlock.Domain.Core.Entities;

/// <summary>Ordered membership of an asset in a collection. Cascades on hard asset delete.</summary>
public class CollectionItem
{
    public required Guid CollectionId { get; set; }
    public required Guid AssetId { get; set; }

    /// <summary>One-based contiguous position within the collection.</summary>
    public required int Position { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public Collection Collection { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
}
