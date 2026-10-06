namespace AssetBlock.Domain.Core.Dto.Moderation;

public sealed record ModerationDraftWorkspaceDto(
    Guid WorkspaceId,
    Guid ScopeId,
    Guid AssetId,
    Guid? AssetVersionId,
    long WorkspaceRevision,
    long CaseRevision);

public sealed record ModerationDraftSaveRequest(
    Guid ActorUserId,
    Guid AssetId,
    Guid WorkspaceId,
    long ExpectedWorkspaceRevision,
    Guid OperationId,
    string RequestDigest,
    string PayloadJson,
    int SchemaVersion,
    string ContentDigest,
    Guid AuthorUserId);

public enum ModerationDraftSaveStatus
{
    SUCCEEDED,
    STALE_WORKSPACE,
    OPERATION_CONFLICT,
    NOT_FOUND,
    CROSS_ASSET,
    FORBIDDEN
}

public sealed record ModerationDraftSaveResult(
    ModerationDraftSaveStatus Status,
    long WorkspaceRevision,
    long? MetadataRevision = null,
    string? IdempotentResultJson = null);

public sealed record JsonIdempotencyReplay(string ResultJson);

public enum ModerationSubmissionAttemptStatus
{
    BLOCKED_ANALYSIS_UNAVAILABLE,
    BLOCKED_UNTRUSTED_REPORT,
    ACTIVE_CASE_EXISTS,
    STALE_WORKSPACE,
    OPERATION_CONFLICT,
    SUCCEEDED,
    NOT_FOUND
}

public sealed record ModerationSubmissionAttemptResult(
    ModerationSubmissionAttemptStatus Status,
    Guid? SubmissionId = null,
    string? IdempotentResultJson = null);

public enum ModeratorScopedProbeStatus
{
    DENIED_ROLE,
    SUCCEEDED,
    NOT_FOUND
}

public sealed record ModeratorScopedProbeResult(ModeratorScopedProbeStatus Status, long? WorkspaceRevision = null);
