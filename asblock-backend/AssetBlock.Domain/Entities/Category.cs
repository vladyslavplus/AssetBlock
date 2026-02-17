using AssetBlock.Domain.Primitives.BaseEntities;

namespace AssetBlock.Domain.Entities;

public class Category : BaseEntity
{
    public required string Name { get; set; }
    public string? Description { get; set; }
    public required string Slug { get; set; }

    public ICollection<Asset> Assets { get; set; } = new List<Asset>();
}
