using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Moderation;

public sealed record CreateAssetDraftRequest(
    Guid OwnerUserId,
    Guid OperationId,
    string Title,
    string? Description,
    Guid CategoryId,
    decimal Price,
    int? DownloadLimitPerHour,
    Audit.AuditEvent? AuditEvent = null);

public sealed record DraftRevisionSaveRequest(
    Guid ActorUserId,
    Guid AssetId,
    Guid? AssetVersionId,
    string OperationKind,
    Guid OperationId,
    string RequestDigest,
    string PayloadJson,
    int SchemaVersion,
    string ContentDigest,
    long ExpectedWorkspaceRevision,
    Audit.AuditEvent? AuditEvent = null);

public sealed record WithdrawSubmissionRequest(
    Guid ActorUserId,
    Guid SubmissionId,
    long ExpectedCaseRevision,
    Guid OperationId,
    string RequestDigest,
    ModerationWithdrawalReason WithdrawalReason,
    Audit.AuditEvent? AuditEvent = null);

public enum WithdrawSubmissionStatus
{
    SUCCEEDED,
    NOT_FOUND,
    FORBIDDEN,
    STALE_CASE,
    OPERATION_CONFLICT,
    NOT_ACTIVE
}

public sealed record WithdrawSubmissionResult(
    WithdrawSubmissionStatus Status,
    long? CaseRevision = null,
    string? IdempotentResultJson = null,
    bool Replayed = false);

public enum VersionAttachStatus
{
    ATTACHED,
    CONFLICT,
    FORBIDDEN
}

public sealed record VersionAttachResult(
    VersionAttachStatus Status,
    AssetDraftWorkspaceSnapshot? VersionWorkspace = null,
    int? MaterialHeadRevision = null,
    int? DeclarationHeadRevision = null);
