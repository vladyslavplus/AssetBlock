namespace AssetBlock.Domain.Core.Entities;

/// <summary>Durable listing cursor and lease for the bounded storage orphan sweep.</summary>
public sealed class StorageOrphanCleanupState
{
    public int Id { get; init; } = 1;
    public string? CursorKey { get; set; }
    public string? CursorVersionId { get; set; }
    public Guid? LeaseId { get; set; }
    public DateTimeOffset? LeaseUntil { get; set; }
}
