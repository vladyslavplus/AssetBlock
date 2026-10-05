using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IModerationFoundationStore
{
    Task<AssetDraftWorkspaceSnapshot?> LockAssetAndWorkspaceForUpdate(
        Guid assetId,
        Guid workspaceId,
        long expectedWorkspaceRevision,
        CancellationToken cancellationToken = default);

    Task<UserPersistedRole?> LockRoleAfterWorkspace(
        Guid userId,
        CancellationToken cancellationToken = default);

    Task<AssetDraftWorkspaceSnapshot> EnsurePreUploadWorkspace(
        Guid assetId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task<IdempotentMutationResult> TryCommitIdempotentMutation(
        Guid actorUserId,
        string operationKind,
        Guid operationId,
        string requestDigest,
        string resultJson,
        CancellationToken cancellationToken = default);

    Task<GuardedSubmissionResult> AttemptGuardedSubmission(
        GuardedSubmissionRequest request,
        CancellationToken cancellationToken = default);

    Task<ModerationCaseAccessResult> GetCaseSummaryForModerator(
        Guid moderatorUserId,
        Guid submissionId,
        CancellationToken cancellationToken = default);
}
