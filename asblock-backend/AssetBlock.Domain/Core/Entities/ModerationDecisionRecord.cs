using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>Terminal moderation decision identity linked to a submission.</summary>
public class ModerationDecisionRecord : BaseEntity
{
    public required Guid SubmissionId { get; init; }
    public required Guid AssetId { get; init; }
    public required Guid AssetVersionId { get; init; }
    public required Guid ModeratorUserId { get; init; }
    public required ModerationSubmissionState Outcome { get; init; }
    public Guid? PublicationSnapshotId { get; init; }
    public required long CaseRevision { get; init; }
    public required string Message { get; init; }

    public ModerationSubmission Submission { get; set; } = null!;
    public PublicationSnapshot? PublicationSnapshot { get; set; }
}
