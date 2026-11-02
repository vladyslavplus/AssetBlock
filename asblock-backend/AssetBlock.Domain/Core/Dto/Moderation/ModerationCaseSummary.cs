using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Moderation;

public sealed record ModerationCaseSummary(
    Guid SubmissionId,
    Guid AssetId,
    Guid AssetVersionId,
    ModerationSubmissionState State,
    ModerationSubmissionKind Kind,
    long CaseRevision,
    string ContentSha256,
    string PolicyVersion,
    DecisionBasis Basis,
    bool ManualPlanCompletionReferenceRequired,
    ModeratorReportEvidence? RetrievalEvidence = null, Guid? CoverageAssessmentId = null,
    bool IsClaimedByCaller = false);
