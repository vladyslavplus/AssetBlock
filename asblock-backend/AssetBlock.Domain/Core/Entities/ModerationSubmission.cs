using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>Immutable submission snapshot plus mutable active-case state for a version.</summary>
public class ModerationSubmission : BaseEntity
{
    public required Guid AssetId { get; init; }
    public required Guid AssetVersionId { get; init; }
    public required Guid WorkspaceId { get; init; }
    /// <summary>Workspace version-scope key; must match linked workspace and <see cref="AssetVersionId"/>.</summary>
    public required Guid WorkspaceVersionScopeKey { get; init; }
    public required Guid OwnerUserId { get; init; }

    public required string ContentSha256 { get; init; }
    public Guid? CodeAnalysisReportHeaderId { get; init; }
    public required int DeclarationRevision { get; init; }
    public required int MaterialMetadataRevision { get; init; }
    public required int SellerEvidenceRevision { get; init; }
    public required string PolicyVersion { get; init; }
    public required string SellerEvidenceDigest { get; init; }

    public required ModerationSubmissionState State { get; set; }
    public ModerationWithdrawalReason? WithdrawalReason { get; set; }
    public required long CaseRevision { get; set; }
    public Guid? PreviousSubmissionId { get; init; }

    public Asset Asset { get; set; } = null!;
    public AssetVersion AssetVersion { get; set; } = null!;
    public AssetDraftWorkspace Workspace { get; set; } = null!;
    public CodeAnalysisReportHeader? CodeAnalysisReportHeader { get; set; }
    public ModerationSubmission? PreviousSubmission { get; set; }
}
