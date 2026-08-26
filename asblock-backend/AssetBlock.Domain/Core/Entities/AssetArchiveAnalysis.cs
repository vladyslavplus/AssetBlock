namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Bounded 1-to-1 archive inspection and manifest analysis record associated with an asset version.
/// Used by downstream AI copilot and security auditing without unrestricted archive extraction.
/// </summary>
public class AssetArchiveAnalysis
{
    public required Guid AssetVersionId { get; set; }
    public required int FileCount { get; set; }
    public required long TotalExpandedBytes { get; set; }
    public string? ReadmeContent { get; set; }
    public string? ManifestMetadata { get; set; }
    public required DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }

    public AssetVersion AssetVersion { get; set; } = null!;
}
