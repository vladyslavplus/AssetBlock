using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public class ModerationSubmissionHistoryEntry : BaseEntity
{
    public required Guid SubmissionId { get; init; }
    public required Guid AssetId { get; init; }
    public required Guid AssetVersionId { get; init; }
    public required ModerationSubmissionState State { get; init; }
    public ModerationWithdrawalReason? WithdrawalReason { get; init; }
    public required long CaseRevision { get; init; }
    public required Guid ActorUserId { get; init; }
    public required string Summary { get; init; }

    public ModerationSubmission Submission { get; set; } = null!;
}
