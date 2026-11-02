using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IModerationFoundationStore
{
    Task<GuardedSubmissionResult> RequestAppeal(AppealCreateRequest request, CancellationToken cancellationToken = default);
    Task<ModerationDecisionResult> DecideModerationCase(ModerationDecisionRequest request, CancellationToken cancellationToken = default);
    Task<CaseClaimResult> ChangeCaseClaim(CaseClaimChange request, CancellationToken cancellationToken = default);
    Task<ReviewAccessResult<ModeratorQueuePage>> GetModeratorQueue(ModeratorQueueRequest request, CancellationToken cancellationToken = default);
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

    /// <summary>Creates an owned asset + pre-upload workspace atomically. Returns null when the operation id replays with a different payload.</summary>
    Task<SellerDraftCreatedDto?> CreateAssetDraft(
        CreateAssetDraftRequest request,
        CancellationToken cancellationToken = default);

    Task<SellerDraftSnapshotDto?> GetOwnerDraftSnapshot(
        Guid assetId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    /// <summary>Resolves an owned draft workspace by id; null when missing, foreign, or not a pre-upload workspace.</summary>
    Task<SellerDraftSnapshotDto?> GetOwnerDraftSnapshotByWorkspace(
        Guid workspaceId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task<ModerationDraftSaveResult> SaveDraftRevision(
        DraftRevisionSaveRequest request,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads the owner's declaration snapshot for the requested scope. Returns null when the
    /// workspace itself is missing or not owned; a present workspace without stored declaration
    /// content returns a snapshot with a null Declaration.
    /// </summary>
    Task<SellerDeclarationSnapshotDto?> GetOwnerDeclarationSnapshot(
        Guid assetId,
        Guid? assetVersionId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task<SellerVersionReviewDto?> GetOwnerVersionReview(
        Guid assetVersionId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task<VersionAttachResult> AttachUploadedVersionToWorkspace(
        Guid assetId,
        Guid versionId,
        Guid ownerUserId,
        Guid workspaceId,
        long expectedWorkspaceRevision,
        CancellationToken cancellationToken = default);

    Task<ReviewAccessResult<RestrictedEvidenceDto>> ReadRestrictedEvidence(Guid actorId, Guid scopeId, bool coverageScope, RestrictedEvidenceKind kind, Guid? findingId, CancellationToken cancellationToken = default);
    Task<ReviewAccessResult<CoverageReviewSaved>> RequestCoverageReview(Guid actorId, Guid assetId, Guid versionId, CoverageReviewCreateRequest request, string requestDigest, CancellationToken cancellationToken = default);
    Task<ReviewAccessResult<CoverageReviewSaved>> AssessCoverage(Guid actorId, Guid requestId, CoverageAssessmentRequest request, string requestDigest, CancellationToken cancellationToken = default);
    Task<ReviewAccessResult<CoverageQueuePage>> GetCoverageQueue(Guid actorId, int page, CancellationToken cancellationToken = default);

    Task<SellerEvidenceWriteResult> SaveSellerEvidence(SellerEvidenceWriteRequest request, CancellationToken cancellationToken = default);
    Task<SellerResponsesPageDto?> GetOwnerResponses(Guid assetId, Guid assetVersionId, Guid ownerUserId, int page, CancellationToken cancellationToken = default);
    /// <summary>Caller owns the transaction and its receipt. No standalone decision or publication operation.</summary>
    Task<bool> AppendFindingDispositions(Guid submissionId, Guid moderatorUserId, long expectedCaseRevision,
        IReadOnlyList<FindingDispositionInput> dispositions, CancellationToken cancellationToken = default);

    Task<WithdrawSubmissionResult> WithdrawSubmission(
        WithdrawSubmissionRequest request,
        CancellationToken cancellationToken = default);
}
