using System.Text.Json;
using System.Text.Json.Serialization;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class ModerationFoundationStore(
    ApplicationDbContext dbContext,
    IAssetStore assetStore,
    IUserStore userStore,
    TimeProvider? timeProvider = null) : IModerationFoundationStore
{
    private static readonly JsonSerializerOptions _idempotencyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<AssetDraftWorkspaceSnapshot?> LockAssetAndWorkspaceForUpdate(
        Guid assetId,
        Guid workspaceId,
        long expectedWorkspaceRevision,
        CancellationToken cancellationToken = default)
    {
        Asset? asset = await assetStore.GetForUpdate(assetId, cancellationToken);
        if (asset is null)
        {
            return null;
        }

        Guid lockedWorkspaceId = await dbContext.Database
            .SqlQuery<Guid>($"""
                SELECT "Id" AS "Value"
                FROM asset_draft_workspaces
                WHERE "Id" = {workspaceId} AND "AssetId" = {assetId}
                FOR UPDATE
                """)
            .FirstOrDefaultAsync(cancellationToken);

        if (lockedWorkspaceId == Guid.Empty)
        {
            return null;
        }

        AssetDraftWorkspace? workspace = await dbContext.AssetDraftWorkspaces
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);

        if (workspace is null || workspace.WorkspaceRevision != expectedWorkspaceRevision)
        {
            return null;
        }

        return ToSnapshot(workspace);
    }

    public Task<UserPersistedRole?> LockRoleAfterWorkspace(Guid userId, CancellationToken cancellationToken = default)
    {
        return userStore.LockAndReadPersistedRole(userId, cancellationToken);
    }

    public async Task<AssetDraftWorkspaceSnapshot> EnsurePreUploadWorkspace(
        Guid assetId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        AssetOwnershipDto? ownership = await assetStore.GetOwnership(assetId, cancellationToken);
        if (ownership is null || ownership.AuthorId != ownerUserId || ownership.IsDeleted)
        {
            throw new InvalidOperationException("Asset not found or not owned.");
        }

        AssetDraftWorkspace? existing = await dbContext.AssetDraftWorkspaces
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.AssetId == assetId && w.AssetVersionId == null, cancellationToken);

        if (existing is not null)
        {
            return ToSnapshot(existing);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        var workspace = new AssetDraftWorkspace
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            AssetVersionId = null,
            WorkspaceVersionScopeKey = DraftWorkspaceVersionScopes.PreUpload,
            ScopeId = Guid.NewGuid(),
            CreatedAt = now,
            WorkspaceRevision = 1,
            CaseRevision = 1
        };

        dbContext.AssetDraftWorkspaces.Add(workspace);
        await dbContext.SaveChangesAsync(cancellationToken);
        return ToSnapshot(workspace);
    }

    public async Task<IdempotentMutationResult> TryCommitIdempotentMutation(
        Guid actorUserId,
        string operationKind,
        Guid operationId,
        string requestDigest,
        string resultJson,
        CancellationToken cancellationToken = default)
    {
        JsonMutationIdempotencyRecord? existing = await dbContext.JsonMutationIdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.ActorUserId == actorUserId
                && r.OperationKind == operationKind
                && r.OperationId == operationId,
                cancellationToken);

        if (existing is not null)
        {
            if (existing.RequestDigest == requestDigest)
            {
                return new IdempotentMutationResult(IdempotentMutationStatus.REPLAYED, existing.ResultJson);
            }

            return new IdempotentMutationResult(IdempotentMutationStatus.CONFLICT, null);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        var record = new JsonMutationIdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorUserId = actorUserId,
            OperationKind = operationKind,
            OperationId = operationId,
            RequestDigest = requestDigest,
            ResultJson = resultJson,
            CreatedAt = now
        };

        dbContext.JsonMutationIdempotencyRecords.Add(record);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return new IdempotentMutationResult(IdempotentMutationStatus.COMMITTED, resultJson);
        }
        catch (DbUpdateException ex) when (IsIdempotencyOperationUniqueViolation(ex))
        {
            dbContext.Entry(record).State = EntityState.Detached;
            JsonMutationIdempotencyRecord raced = await dbContext.JsonMutationIdempotencyRecords
                .AsNoTracking()
                .FirstAsync(r =>
                    r.ActorUserId == actorUserId
                    && r.OperationKind == operationKind
                    && r.OperationId == operationId,
                    cancellationToken);

            if (raced.RequestDigest == requestDigest)
            {
                return new IdempotentMutationResult(IdempotentMutationStatus.REPLAYED, raced.ResultJson);
            }

            return new IdempotentMutationResult(IdempotentMutationStatus.CONFLICT, null);
        }
    }

    public async Task<GuardedSubmissionResult> AttemptGuardedSubmission(
        GuardedSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        AssetDraftWorkspaceSnapshot? workspace = await LockAssetAndWorkspaceForUpdate(
            request.AssetId,
            request.WorkspaceId,
            request.ExpectedWorkspaceRevision,
            cancellationToken);

        if (workspace is null)
        {
            return new GuardedSubmissionResult(GuardedSubmissionStatus.CONFLICT, null, ErrorCodes.ERR_MODERATION_WORKSPACE_STALE);
        }

        if (workspace.CaseRevision != request.ExpectedCaseRevision)
        {
            return new GuardedSubmissionResult(GuardedSubmissionStatus.CONFLICT, null, ErrorCodes.ERR_MODERATION_WORKSPACE_STALE);
        }

        if (workspace.WorkspaceVersionScopeKey != request.AssetVersionId)
        {
            return new GuardedSubmissionResult(GuardedSubmissionStatus.CONFLICT, null, ErrorCodes.ERR_MODERATION_WORKSPACE_STALE);
        }

        AssetOwnershipDto? ownership = await assetStore.GetOwnership(request.AssetId, cancellationToken);
        if (ownership is null || ownership.IsDeleted || ownership.AuthorId != request.OwnerUserId)
        {
            return new GuardedSubmissionResult(GuardedSubmissionStatus.BLOCKED, null, ErrorCodes.ERR_FORBIDDEN);
        }

        UserPersistedRole? ownerRole = await LockRoleAfterWorkspace(request.OwnerUserId, cancellationToken);
        if (ownerRole is null
            || ownerRole.Role is not (AppRoles.USER or AppRoles.MODERATOR or AppRoles.ADMIN))
        {
            return new GuardedSubmissionResult(GuardedSubmissionStatus.BLOCKED, null, ErrorCodes.ERR_FORBIDDEN);
        }

        JsonMutationIdempotencyRecord? existingIdempotency = await dbContext.JsonMutationIdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.ActorUserId == request.OwnerUserId
                && r.OperationKind == ModerationOperationKinds.SUBMISSION_CREATE
                && r.OperationId == request.OperationId,
                cancellationToken);

        if (existingIdempotency is not null)
        {
            if (existingIdempotency.RequestDigest != request.RequestDigest)
            {
                return new GuardedSubmissionResult(
                    GuardedSubmissionStatus.CONFLICT,
                    null,
                    ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT);
            }

            return DeserializeGuardedSubmissionResult(existingIdempotency.ResultJson);
        }

        GuardedSubmissionResult outcome = await EvaluateGuardedSubmissionGates(request, cancellationToken);
        var resultJson = SerializeGuardedSubmissionResult(outcome);

        IdempotentMutationResult idempotency = await TryCommitIdempotentMutation(
            request.OwnerUserId,
            ModerationOperationKinds.SUBMISSION_CREATE,
            request.OperationId,
            request.RequestDigest,
            resultJson,
            cancellationToken);

        if (idempotency.Status == IdempotentMutationStatus.CONFLICT)
        {
            return new GuardedSubmissionResult(GuardedSubmissionStatus.CONFLICT, null, ErrorCodes.ERR_MODERATION_OPERATION_CONFLICT);
        }

        if (idempotency.Status == IdempotentMutationStatus.REPLAYED)
        {
            return DeserializeGuardedSubmissionResult(idempotency.ResultJson!);
        }

        return outcome;
    }

    public async Task<ModerationCaseAccessResult> GetCaseSummaryForModerator(
        Guid moderatorUserId,
        Guid submissionId,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.ModerationSubmissions
            .AsNoTracking()
            .Where(s => s.Id == submissionId)
            .Select(s => new
            {
                Summary = new ModerationCaseSummary(
                    s.Id,
                    s.AssetId,
                    s.AssetVersionId,
                    s.State,
                    s.CaseRevision,
                    s.ContentSha256,
                    s.PolicyVersion),
                s.Asset.AuthorId
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return new ModerationCaseAccessResult(ModerationCaseAccessStatus.NOT_FOUND, null);
        }

        if (row.AuthorId == moderatorUserId)
        {
            return new ModerationCaseAccessResult(ModerationCaseAccessStatus.SELF_OWNED_DENIED, null);
        }

        return new ModerationCaseAccessResult(ModerationCaseAccessStatus.FOUND, row.Summary);
    }

    private async Task<GuardedSubmissionResult> EvaluateGuardedSubmissionGates(
        GuardedSubmissionRequest request,
        CancellationToken cancellationToken)
    {
        if (request.CodeAnalysisReportHeaderId is null)
        {
            return new GuardedSubmissionResult(
                GuardedSubmissionStatus.BLOCKED,
                null,
                ErrorCodes.ERR_MODERATION_SUBMISSION_BLOCKED);
        }

        CodeAnalysisReportHeader? report = await dbContext.CodeAnalysisReportHeaders
            .AsNoTracking()
            .FirstOrDefaultAsync(r => r.Id == request.CodeAnalysisReportHeaderId, cancellationToken);

        if (report is null
            || report.AssetId != request.AssetId
            || report.AssetVersionId != request.AssetVersionId
            || !report.IsFinalized
            || report.Purpose != CodeAnalysisReportPurpose.PRODUCTION
            || !report.CanAuthorizePublication)
        {
            return new GuardedSubmissionResult(
                GuardedSubmissionStatus.BLOCKED,
                null,
                ErrorCodes.ERR_ANALYSIS_REPORT_NOT_TRUSTED);
        }

        return new GuardedSubmissionResult(
            GuardedSubmissionStatus.BLOCKED,
            null,
            ErrorCodes.ERR_MODERATION_SUBMISSION_BLOCKED);
    }

    private static bool IsIdempotencyOperationUniqueViolation(DbUpdateException exception)
    {
        return exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation } postgres
               && (postgres.ConstraintName == JsonMutationIdempotencyRecordConfiguration.UNIQUE_OPERATION_INDEX
                   || postgres.ConstraintName?.Contains("ActorUserId_OperationKind_OperationId", StringComparison.Ordinal) == true);
    }

    private static string SerializeGuardedSubmissionResult(GuardedSubmissionResult result) =>
        JsonSerializer.Serialize(
            new GuardedSubmissionIdempotencyPayload(result.Status, result.SubmissionId, result.BlockedReasonCode),
            _idempotencyJsonOptions);

    private static GuardedSubmissionResult DeserializeGuardedSubmissionResult(string resultJson)
    {
        GuardedSubmissionIdempotencyPayload payload = JsonSerializer.Deserialize<GuardedSubmissionIdempotencyPayload>(
            resultJson,
            _idempotencyJsonOptions)
            ?? throw new InvalidOperationException("Idempotency payload is missing.");

        return new GuardedSubmissionResult(payload.Status, payload.SubmissionId, payload.BlockedReasonCode);
    }

    private static AssetDraftWorkspaceSnapshot ToSnapshot(AssetDraftWorkspace workspace) =>
        new(
            workspace.Id,
            workspace.AssetId,
            workspace.AssetVersionId,
            workspace.WorkspaceVersionScopeKey,
            workspace.WorkspaceRevision,
            workspace.CaseRevision,
            workspace.MaterialMetadataHeadRevision,
            workspace.SourceDeclarationHeadRevision,
            workspace.SellerEvidenceHeadRevision);

    private sealed record GuardedSubmissionIdempotencyPayload(
        GuardedSubmissionStatus Status,
        Guid? SubmissionId,
        string? BlockedReasonCode);
}
