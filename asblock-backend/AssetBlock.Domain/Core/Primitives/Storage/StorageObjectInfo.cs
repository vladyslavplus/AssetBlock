namespace AssetBlock.Domain.Core.Primitives.Storage;

public sealed record StorageObjectInfo(
    string Key,
    DateTimeOffset? LastModified,
    long Size,
    string? VersionId = null,
    bool IsDeleteMarker = false);
