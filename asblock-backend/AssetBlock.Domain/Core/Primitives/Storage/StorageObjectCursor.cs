namespace AssetBlock.Domain.Core.Primitives.Storage;

/// <summary>Provider key-marker and version-id-marker for the next ListObjectVersions page.</summary>
public sealed record StorageObjectCursor(string Key, string? VersionId);
