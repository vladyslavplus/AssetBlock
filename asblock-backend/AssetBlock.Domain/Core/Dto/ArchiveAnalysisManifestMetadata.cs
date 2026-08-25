using System.Text.Json.Serialization;

namespace AssetBlock.Domain.Core.Dto;

/// <summary>
/// Bounded metadata extracted from a recognized package/project manifest (e.g. package.json, Cargo.toml, pyproject.toml, .csproj).
/// Contains only clean, safe string identifiers and dependency lists without arbitrary script execution or polymorphic payloads.
/// </summary>
public sealed record RecognizedManifestItem(
    [property: JsonPropertyName("fileName")] string FileName,
    [property: JsonPropertyName("manifestType")] string ManifestType,
    [property: JsonPropertyName("packageName")] string? PackageName = null,
    [property: JsonPropertyName("packageVersion")] string? PackageVersion = null,
    [property: JsonPropertyName("description")] string? Description = null,
    [property: JsonPropertyName("dependencies")] IReadOnlyList<string>? Dependencies = null
);

/// <summary>
/// Top-level bounded container for recognized manifests in an archive.
/// Serialized into jsonb column with strict 16 KiB total byte size limit.
/// </summary>
public sealed record ArchiveAnalysisManifestMetadata(
    [property: JsonPropertyName("manifests")] IReadOnlyList<RecognizedManifestItem> Manifests
)
{
    public static readonly ArchiveAnalysisManifestMetadata Empty = new([]);
}
