namespace AssetBlock.Domain.Primitives.BaseEntities;

/// <summary>
/// Base entity with audit fields for entities that support Created/Updated timestamps.
/// </summary>
public abstract class BaseEntity
{
    public required Guid Id { get; init; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
}
