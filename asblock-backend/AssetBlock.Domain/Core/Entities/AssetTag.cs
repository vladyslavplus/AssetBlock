namespace AssetBlock.Domain.Core.Entities;

public class AssetTag
{
    public Guid AssetId { get; set; }
    public Asset Asset { get; set; } = null!;

    public Guid TagId { get; set; }
    public Tag Tag { get; set; } = null!;
}
