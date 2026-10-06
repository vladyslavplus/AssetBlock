using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>Immutable approved public metadata identity for an asset version.</summary>
public class PublicationSnapshot : BaseEntity
{
    public required Guid AssetId { get; init; }
    public required Guid AssetVersionId { get; init; }
    public required string ContentSha256 { get; init; }
    public required Guid CodeAnalysisReportHeaderId { get; init; }
    public required Guid ModerationSubmissionId { get; init; }
    public required string PolicyVersion { get; init; }
    public required string ApprovedMetadataJson { get; init; }
    public required string RightsReferenceJson { get; init; }

    public Asset Asset { get; set; } = null!;
    public AssetVersion AssetVersion { get; set; } = null!;
    public CodeAnalysisReportHeader CodeAnalysisReportHeader { get; set; } = null!;
    public ModerationSubmission ModerationSubmission { get; set; } = null!;
}
