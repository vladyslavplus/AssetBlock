using AssetBlock.Domain.Core.Enums;
namespace AssetBlock.Domain.Core.Dto.Moderation;

public sealed record CoverageReviewCreateRequest(Guid OperationId, Guid WorkspaceId, Guid ReportId, long ExpectedWorkspaceRevision);
public sealed record CoverageScopeItem(string Path, string Language, string RoleInCoreLogic, bool Readable, RestrictedEvidenceKind EvidenceKind, Guid? FindingId = null);
public sealed record CoverageAssessmentRequest(Guid OperationId, long ExpectedRequestRevision, string ExpectedTupleDigest,
    CoverageAssessmentOutcome Outcome, IReadOnlyList<string> Competence, IReadOnlyList<CoverageScopeItem> Scope,
    bool ManageableScope, string ReviewPlan, string CompletedPlanReference, string Rationale);
public sealed record CoverageOwnerStatus(Guid? RequestId, string Status, string? ReasonCode, bool CanRequest = false);
public sealed record CoverageReviewSaved(Guid RequestId, long RequestRevision, long WorkspaceRevision, string TupleDigest, bool Replayed = false);
public sealed record CoverageQueueItem(Guid RequestId, Guid AssetVersionId, Guid ReportId, long RequestRevision, string TupleDigest, DateTimeOffset CreatedAt);
public sealed record CoverageQueuePage(IReadOnlyList<CoverageQueueItem> Items, int Page, int PageSize, int TotalCount, int TotalPages);
public sealed record RestrictedEvidenceDto(Guid ScopeId, Guid ReportId, RestrictedEvidenceKind Kind, Guid? FindingId, string Content, int ItemCount, bool Truncated);
public enum ReviewAccessStatus { SUCCESS, FORBIDDEN, NOT_FOUND, STALE, CONFLICT, INVALID, SECURITY_RESTRICTED, AUDIT_UNAVAILABLE, SCOPE_UNAVAILABLE }
public sealed record ReviewAccessResult<T>(ReviewAccessStatus Status, T? Value = default, string? ReasonCode = null);
