using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Bounded internal artifact retained from a verified stage so an unchanged declaration-only change
/// can reuse it without running the tool again. Carries no raw source and no arbitrary fetch target.
/// </summary>
public class CodeAnalysisReuseArtifact : BaseEntity
{
    public required Guid SourceReportId { get; set; }

    public required Guid SourceRunId { get; set; }

    public required Guid SourceStageId { get; set; }

    public required string Stage { get; set; }

    /// <summary>Hash of the exact artifact content, verified before any reuse.</summary>
    public required string ArtifactDigest { get; set; }

    /// <summary>Bounded artifact projection; never raw source or private reference identity.</summary>
    public required string ArtifactJson { get; set; }

    public required int PayloadBytes { get; set; }
}
