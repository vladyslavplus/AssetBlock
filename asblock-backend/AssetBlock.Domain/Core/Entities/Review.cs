using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public class Review : BaseEntity
{
    public required Guid AssetId { get; set; }
    public required Guid UserId { get; set; }
    public required int Rating { get; set; }
    public string? Comment { get; set; }

    public Asset Asset { get; set; } = null!;
    public User User { get; set; } = null!;
}
