using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public class Tag : BaseEntity
{
    public required string Name { get; set; }

    public ICollection<AssetTag> AssetTags { get; set; } = [];
}
