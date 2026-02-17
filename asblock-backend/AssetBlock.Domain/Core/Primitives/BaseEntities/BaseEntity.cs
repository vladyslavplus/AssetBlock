namespace AssetBlock.Domain.Core.Primitives.BaseEntities;

/// <summary>
/// Base entity with audit fields for entities that support Created/Updated timestamps.
/// </summary>
public abstract class BaseEntity
{
    public required Guid Id { get; init; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
    public DateTimeOffset UpdatedAt { get; set; }
}
