using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Moderation;

public sealed record CaseClaimSaved(Guid SubmissionId, ModerationSubmissionState State, Guid? HolderId, long CaseRevision, bool Replayed = false);
public sealed record CaseClaimChange(Guid ActorId, Guid SubmissionId, Guid OperationId, long ExpectedCaseRevision, bool Claim, string Digest);
public enum CaseClaimStatus { APPEAL_REVIEWER_EXCLUDED, SUCCESS, FORBIDDEN, NOT_FOUND, STALE, HELD, NOT_ACTIVE, NOT_CLAIMED, NOT_HOLDER, OPERATION_CONFLICT, AUDIT_UNAVAILABLE }
public sealed record CaseClaimResult(CaseClaimStatus Status, CaseClaimSaved? Value = null);
public enum CaseClaimFilter { ALL, MINE, UNCLAIMED }
public sealed record ModeratorQueueRequest(Guid ActorId, ModerationSubmissionState? State, CaseClaimFilter ClaimFilter, int PageSize, string? Cursor);
public sealed record ModeratorQueueItem(Guid SubmissionId, Guid AssetId, string AssetTitle, int VersionNumber, DateTimeOffset SubmittedAt,
    ModerationSubmissionKind Kind, ModerationSubmissionState State, Guid? ClaimHolderId, string? ClaimHolderName, long CaseRevision,
    CodeAnalysisCoverageLevel? CoverageLevel, int MaterialFindingCount, bool HasOpenResponses);
public sealed record ModeratorQueuePage(IReadOnlyList<ModeratorQueueItem> Items, string? NextCursor);
