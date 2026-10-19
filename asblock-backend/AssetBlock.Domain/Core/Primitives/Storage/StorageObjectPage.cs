namespace AssetBlock.Domain.Core.Primitives.Storage;

/// <summary>A bounded page from the provider's ListObjectVersions API.</summary>
public sealed record StorageObjectPage(
    IReadOnlyList<StorageObjectInfo> Items,
    StorageObjectCursor? NextCursor,
    bool IsComplete);
