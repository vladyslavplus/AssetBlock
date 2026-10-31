using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Moderation;

public sealed record DecisionBasis(Guid VersionId, string ContentSha256, Guid AnalysisReportId, int DeclarationRevision,
    int MaterialMetadataRevision, string PolicyVersion, int SellerEvidenceRevision, long CaseRevision);
public sealed record ModerationDecisionRequest(Guid ActorId, Guid SubmissionId, Guid OperationId, DecisionBasis Basis,
    ModerationDecisionOutcome Outcome, string Rationale, IReadOnlyList<FindingDispositionInput> Dispositions,
    string? ManualPlanCompletionReference, string RequestDigest);
public enum ModerationDecisionStatus { SUCCEEDED, FORBIDDEN, NOT_FOUND, STALE, NOT_ACTIVE, NOT_HOLDER, OPERATION_CONFLICT, BLOCKED, AUDIT_UNAVAILABLE }
public sealed record ModerationDecisionSaved(Guid DecisionId, ModerationSubmissionState State, Guid? PublicationSnapshotId,
    bool Promoted, long CaseRevision, bool Replayed = false);
public sealed record ModerationDecisionResult(ModerationDecisionStatus Status, ModerationDecisionSaved? Saved = null, string? ReasonCode = null);
