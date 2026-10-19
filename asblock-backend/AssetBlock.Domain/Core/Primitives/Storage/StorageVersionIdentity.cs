using System.Diagnostics.CodeAnalysis;

namespace AssetBlock.Domain.Core.Primitives.Storage;

public static class StorageVersionIdentity
{
    public static bool IsImmutable([NotNullWhen(true)] string? versionId) =>
        !string.IsNullOrWhiteSpace(versionId)
        && !string.Equals(versionId.Trim(), "null", StringComparison.OrdinalIgnoreCase);
}
