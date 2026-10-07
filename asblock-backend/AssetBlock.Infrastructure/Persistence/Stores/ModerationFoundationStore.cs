using System.Text.Json;
using System.Text.Json.Serialization;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Moderation;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Configurations;
using AssetBlock.Infrastructure.Persistence.Publication;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class ModerationFoundationStore(
    ApplicationDbContext dbContext,
    IAssetStore assetStore,
    IUserStore userStore,
    IUnitOfWork unitOfWork,
    TimeProvider? timeProvider = null,
    IAuditWriter? auditWriter = null) : IModerationFoundationStore
{
    private static readonly JsonSerializerOptions _idempotencyJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Enum members stay canonical UPPER_SNAKE_CASE; property names stay camelCase.
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly IAuditWriter? _auditWriter = auditWriter;

    /// <summary>
    /// Writes the mutation's audit entry inside the ambient transaction: an audit failure
    /// rolls the mutation back, and a replay (which committed its audit with the mutation)
    /// must not write a second entry.
    /// </summary>
    private async Task WriteAuditInTransaction(AuditEvent? auditEvent, CancellationToken ct)
    {
        if (auditEvent is null || _auditWriter is null)
        {
            return;
        }

        await _auditWriter.Write(auditEvent, ct);
    }

    /// <summary>
    /// A FOR UPDATE row lock does not refresh an already tracked instance: the tracked values
    /// may predate the lock. Reads fresh values under the lock and adopts them into the tracked
    /// entity (or attaches the fresh read) without detaching unrelated pending changes.
    /// </summary>
    private async Task<AssetDraftWorkspace?> ReadWorkspaceFreshUnderLock(Guid workspaceId, CancellationToken cancellationToken)
    {
        AssetDraftWorkspace? fresh = await dbContext.AssetDraftWorkspaces
            .AsNoTracking()
            .FirstOrDefaultAsync(w => w.Id == workspaceId, cancellationToken);
        if (fresh is null)
        {
            return null;
        }

        AssetDraftWorkspace? tracked = dbContext.AssetDraftWorkspaces.Local
            .FirstOrDefault(w => w.Id == workspaceId);
        if (tracked is null)
        {
            EntityEntry<AssetDraftWorkspace> entry = dbContext.AssetDraftWorkspaces.Attach(fresh);
            entry.State = EntityState.Unchanged;
            return fresh;
        }

        tracked.WorkspaceRevision = fresh.WorkspaceRevision;
        tracked.CaseRevision = fresh.CaseRevision;
        tracked.MaterialMetadataHeadRevision = fresh.MaterialMetadataHeadRevision;
        tracked.SourceDeclarationHeadRevision = fresh.SourceDeclarationHeadRevision;
        tracked.SellerEvidenceHeadRevision = fresh.SellerEvidenceHeadRevision;
        return tracked;
    }

    /// <summary>Same tracked-refresh contract as <see cref="ReadWorkspaceFreshUnderLock"/> for submissions.</summary>
    private async Task<ModerationSubmission?> ReadSubmissionFreshUnderLock(Guid submissionId, CancellationToken cancellationToken)
    {
        ModerationSubmission? fresh = await dbContext.ModerationSubmissions
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == submissionId, cancellationToken);
        if (fresh is null)
        {
            return null;
        }

        ModerationSubmission? tracked = dbContext.ModerationSubmissions.Local
            .FirstOrDefault(s => s.Id == submissionId);
        if (tracked is null)
        {
            EntityEntry<ModerationSubmission> entry = dbContext.ModerationSubmissions.Attach(fresh);
            entry.State = EntityState.Unchanged;
            return fresh;
        }

        tracked.State = fresh.State;
        tracked.CaseRevision = fresh.CaseRevision;
        tracked.WithdrawalReason = fresh.WithdrawalReason;
        return tracked;
    }

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
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is NpgsqlException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // A concurrent request created the same pre-upload workspace; re-read the committed row.
            dbContext.Entry(workspace).State = EntityState.Detached;
            AssetDraftWorkspace? raced = await dbContext.AssetDraftWorkspaces
                .AsNoTracking()
                .FirstOrDefaultAsync(w => w.AssetId == assetId && w.AssetVersionId == null, cancellationToken);
            if (raced is null)
            {
                throw;
            }

            return ToSnapshot(raced);
        }

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

    public async Task<SellerDraftCreatedDto?> CreateAssetDraft(
        CreateAssetDraftRequest request,
        CancellationToken cancellationToken = default)
    {
        // Digest covers the creation payload so a lost response can be replayed safely,
        // while the same operation id with different input is rejected.
        var payloadJson = JsonSerializer.Serialize(
            new
            {
                request.Title,
                request.Description,
                request.CategoryId,
                request.Price,
                request.DownloadLimitPerHour
            },
            _idempotencyJsonOptions);
        var requestDigest = ComputeSha256Hex(payloadJson);

        SellerDraftCreatedDto? outcome = null;
        var conflict = false;
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            // Serialize concurrent creations for the same (actor, operation): the second
            // transaction waits here, then sees the committed receipt and replays instead of
            // racing to insert a duplicate and failing on the unique index.
            var advisoryKey = $"draft-create:{request.OwnerUserId}:{request.OperationId}";
            await dbContext.Database.ExecuteSqlAsync(
                $"SELECT pg_advisory_xact_lock(hashtext({advisoryKey}))", ct);

            JsonMutationIdempotencyRecord? existing = await FindIdempotencyRecord(
                request.OwnerUserId, ModerationOperationKinds.DRAFT_CREATE, request.OperationId, ct);
            if (existing is not null)
            {
                if (existing.RequestDigest != requestDigest)
                {
                    conflict = true;
                    return;
                }

                DraftCreateIdempotencyPayload payload = JsonSerializer.Deserialize<DraftCreateIdempotencyPayload>(
                    existing.ResultJson, _idempotencyJsonOptions)
                    ?? throw new InvalidOperationException("Draft create idempotency payload is missing.");
                outcome = new SellerDraftCreatedDto(payload.AssetId, payload.WorkspaceId, payload.WorkspaceRevision, Replayed: true);
                return;
            }

            DateTimeOffset now = _timeProvider.GetUtcNow();
            var assetId = Guid.NewGuid();

            Asset asset = new()
            {
                Id = assetId,
                AuthorId = request.OwnerUserId,
                CategoryId = request.CategoryId,
                Title = request.Title,
                Description = request.Description,
                Price = request.Price,
                DownloadLimitPerHour = request.DownloadLimitPerHour,
                CreatedAt = now
            };

            var workspaceId = Guid.NewGuid();
            var workspace = new AssetDraftWorkspace
            {
                Id = workspaceId,
                AssetId = assetId,
                AssetVersionId = null,
                WorkspaceVersionScopeKey = DraftWorkspaceVersionScopes.PreUpload,
                ScopeId = Guid.NewGuid(),
                WorkspaceRevision = 1,
                CaseRevision = 1,
                CreatedAt = now
            };

            // Initial immutable material head: snapshot readers and the upload attach always
            // see the created metadata, even if the seller only saves a declaration next.
            var material = new SellerDraftMaterialPayload(request.Title, request.Description, request.CategoryId, Array.Empty<string>());
            var materialJson = JsonSerializer.Serialize(material, _idempotencyJsonOptions);
            var materialDigest = ComputeSha256Hex(materialJson);
            workspace.MaterialMetadataHeadRevision = 1;
            dbContext.AssetMaterialMetadataRevisions.Add(new AssetMaterialMetadataRevision
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                WorkspaceId = workspaceId,
                AssetVersionId = null,
                WorkspaceVersionScopeKey = workspace.WorkspaceVersionScopeKey,
                Revision = 1,
                AuthorUserId = request.OwnerUserId,
                ContentDigest = materialDigest,
                SchemaVersion = SellerDraftLimits.MATERIAL_METADATA_SCHEMA_VERSION,
                PayloadJson = materialJson,
                CreatedAt = now
            });

            dbContext.Assets.Add(asset);
            dbContext.AssetDraftWorkspaces.Add(workspace);
            var result = new SellerDraftCreatedDto(assetId, workspaceId, workspace.WorkspaceRevision);
            dbContext.JsonMutationIdempotencyRecords.Add(new JsonMutationIdempotencyRecord
            {
                Id = Guid.NewGuid(),
                ActorUserId = request.OwnerUserId,
                OperationKind = ModerationOperationKinds.DRAFT_CREATE,
                OperationId = request.OperationId,
                RequestDigest = requestDigest,
                ResultJson = JsonSerializer.Serialize(
                    new DraftCreateIdempotencyPayload(assetId, workspaceId, workspace.WorkspaceRevision),
                    _idempotencyJsonOptions),
                CreatedAt = now
            });

            try
            {
                // The created asset id is only known here; attach it to the audit resource.
                await WriteAuditInTransaction(
                    request.AuditEvent is null || request.AuditEvent.ResourceId is not null
                        ? request.AuditEvent
                        : request.AuditEvent with { ResourceId = assetId.ToString() },
                    ct);
                await dbContext.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsIdempotencyOperationUniqueViolation(ex))
            {
                // Defensive: identical (actor, operation) creations are serialized by the
                // advisory lock, so this only fires for foreign-key-shaped races.
                conflict = true;
                return;
            }

            outcome = result;
        }, cancellationToken);

        return conflict ? null : outcome!;
    }

    /// <summary>Resolves an owned pre-upload draft workspace by id for the upload-into-draft flow.</summary>
    public async Task<SellerDraftSnapshotDto?> GetOwnerDraftSnapshotByWorkspace(
        Guid workspaceId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.AssetDraftWorkspaces
            .AsNoTracking()
            .Where(w => w.Id == workspaceId && w.AssetVersionId == null)
            .Join(dbContext.Assets.AsNoTracking(),
                w => w.AssetId,
                a => a.Id,
                (w, a) => new { Workspace = w, a.AuthorId, a.DeletedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null || row.AuthorId != ownerUserId || row.DeletedAt.HasValue)
        {
            return null;
        }

        MaterialAndDeclaration? heads = await LoadLatestHeads(row.Workspace.AssetId, workspaceId, cancellationToken);
        var latestVersion = await dbContext.AssetVersions
            .AsNoTracking()
            .Where(v => v.AssetId == row.Workspace.AssetId)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new { Id = (Guid?)v.Id, Number = (int?)v.VersionNumber })
            .FirstOrDefaultAsync(cancellationToken);

        SellerDraftMaterialPayload material = heads?.Material
            ?? new SellerDraftMaterialPayload(string.Empty, null, Guid.Empty, Array.Empty<string>());
        return new SellerDraftSnapshotDto(
            row.Workspace.AssetId,
            workspaceId,
            row.Workspace.WorkspaceRevision,
            row.Workspace.CaseRevision,
            material,
            heads?.Declaration,
            IsDeclarationComplete(heads?.Declaration),
            latestVersion?.Id,
            latestVersion?.Number);
    }

    public async Task<SellerDraftSnapshotDto?> GetOwnerDraftSnapshot(
        Guid assetId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        AssetOwnershipDto? ownership = await assetStore.GetOwnership(assetId, cancellationToken);
        if (ownership is null || ownership.AuthorId != ownerUserId)
        {
            return null;
        }

        AssetDraftWorkspace? workspace = await dbContext.AssetDraftWorkspaces
            .AsNoTracking()
            .SingleOrDefaultAsync(w => w.AssetId == assetId && w.AssetVersionId == null, cancellationToken);
        if (workspace is null)
        {
            return null;
        }

        MaterialAndDeclaration? heads = await LoadLatestHeads(assetId, workspace.Id, cancellationToken);

        var assetRow = await dbContext.Assets
            .AsNoTracking()
            .Where(a => a.Id == assetId)
            .Select(a => new { a.Title, a.Description, a.CategoryId })
            .FirstOrDefaultAsync(cancellationToken);
        if (assetRow is null)
        {
            return null;
        }

        var latestVersion = await dbContext.AssetVersions
            .AsNoTracking()
            .Where(v => v.AssetId == assetId)
            .OrderByDescending(v => v.VersionNumber)
            .Select(v => new { Id = (Guid?)v.Id, Number = (int?)v.VersionNumber })
            .FirstOrDefaultAsync(cancellationToken);

        SourceDeclarationPayload? declaration = heads?.Declaration;
        SellerDraftMaterialPayload material = heads?.Material
            ?? new SellerDraftMaterialPayload(assetRow.Title, assetRow.Description, assetRow.CategoryId, Array.Empty<string>());
        return new SellerDraftSnapshotDto(
            assetId,
            workspace.Id,
            workspace.WorkspaceRevision,
            workspace.CaseRevision,
            material,
            declaration,
            IsDeclarationComplete(declaration),
            latestVersion?.Id,
            latestVersion?.Number);
    }

    public async Task<ModerationDraftSaveResult> SaveDraftRevision(
        DraftRevisionSaveRequest request,
        CancellationToken cancellationToken = default)
    {
        var isDeclaration = request.OperationKind == ModerationOperationKinds.DECLARATION_SAVE;
        if (!isDeclaration && request.OperationKind != ModerationOperationKinds.DRAFT_SAVE)
        {
            return new ModerationDraftSaveResult(ModerationDraftSaveStatus.NOT_FOUND, 0);
        }

        // Fast rejection before locks; never trusted for the write itself.
        AssetOwnershipDto? ownership = await assetStore.GetOwnership(request.AssetId, cancellationToken);
        if (ownership is null || ownership.AuthorId != request.ActorUserId || ownership.IsDeleted)
        {
            return new ModerationDraftSaveResult(ModerationDraftSaveStatus.FORBIDDEN, 0);
        }

        ModerationDraftSaveResult outcome = null!;
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            // Canonical lock order shared with withdrawal and submission: Asset → workspace → submission/case.
            Asset? asset = await assetStore.GetForUpdate(request.AssetId, ct);
            if (asset is null || asset.AuthorId != request.ActorUserId || asset.DeletedAt.HasValue)
            {
                // Ownership and deletion are re-read under the locks, not trusted from the pre-read.
                outcome = new ModerationDraftSaveResult(ModerationDraftSaveStatus.FORBIDDEN, 0);
                return;
            }

            Guid lockedWorkspaceId = await dbContext.Database
                .SqlQuery<Guid>($"""
                    SELECT "Id" AS "Value"
                    FROM asset_draft_workspaces
                    WHERE "AssetId" = {request.AssetId}
                      AND "AssetVersionId" IS NOT DISTINCT FROM {request.AssetVersionId}
                    FOR UPDATE
                    """)
                .FirstOrDefaultAsync(ct);
            if (lockedWorkspaceId == Guid.Empty)
            {
                outcome = new ModerationDraftSaveResult(ModerationDraftSaveStatus.NOT_FOUND, 0);
                return;
            }

            // Re-read under the row lock with a fresh read: a previously tracked instance would
            // carry pre-lock values, so the CAS below must compare against current state.
            AssetDraftWorkspace? workspace = await ReadWorkspaceFreshUnderLock(lockedWorkspaceId, ct);
            if (workspace is null)
            {
                outcome = new ModerationDraftSaveResult(ModerationDraftSaveStatus.NOT_FOUND, 0);
                return;
            }

            // Replay/conflict is decided under the lock and BEFORE the revision CAS, so two racing
            // identical requests both resolve against the same receipt instead of one getting 409.
            JsonMutationIdempotencyRecord? raced = await FindIdempotencyRecord(
                request.ActorUserId, request.OperationKind, request.OperationId, ct);
            if (raced is not null)
            {
                outcome = raced.RequestDigest == request.RequestDigest
                    ? DeserializeDraftSaveResult(raced.ResultJson) with { WorkspaceId = workspace.Id, Replayed = true }
                    : new ModerationDraftSaveResult(ModerationDraftSaveStatus.OPERATION_CONFLICT, 0);
                return;
            }

            if (workspace.WorkspaceRevision != request.ExpectedWorkspaceRevision)
            {
                outcome = new ModerationDraftSaveResult(ModerationDraftSaveStatus.STALE_WORKSPACE, 0);
                return;
            }

            var head = isDeclaration
                ? workspace.SourceDeclarationHeadRevision
                : workspace.MaterialMetadataHeadRevision;
            var newRevision = head + 1;

            DateTimeOffset now = _timeProvider.GetUtcNow();
            Guid assetId = workspace.AssetId;

            // Editing a submitted workspace supersedes its active case atomically before heads move.
            await SupersedeActiveCase(workspace, request.ActorUserId, now, ct);

            if (isDeclaration)
            {
                dbContext.AssetSourceDeclarationRevisions.Add(new AssetSourceDeclarationRevision
                {
                    Id = Guid.NewGuid(),
                    AssetId = assetId,
                    WorkspaceId = workspace.Id,
                    AssetVersionId = workspace.AssetVersionId,
                    WorkspaceVersionScopeKey = workspace.WorkspaceVersionScopeKey,
                    Revision = newRevision,
                    AuthorUserId = request.ActorUserId,
                    ContentDigest = request.ContentDigest,
                    SchemaVersion = request.SchemaVersion,
                    PayloadJson = request.PayloadJson,
                    CreatedAt = now
                });
                workspace.SourceDeclarationHeadRevision = newRevision;
            }
            else
            {
                dbContext.AssetMaterialMetadataRevisions.Add(new AssetMaterialMetadataRevision
                {
                    Id = Guid.NewGuid(),
                    AssetId = assetId,
                    WorkspaceId = workspace.Id,
                    AssetVersionId = workspace.AssetVersionId,
                    WorkspaceVersionScopeKey = workspace.WorkspaceVersionScopeKey,
                    Revision = newRevision,
                    AuthorUserId = request.ActorUserId,
                    ContentDigest = request.ContentDigest,
                    SchemaVersion = request.SchemaVersion,
                    PayloadJson = request.PayloadJson,
                    CreatedAt = now
                });
                workspace.MaterialMetadataHeadRevision = newRevision;
            }

            workspace.WorkspaceRevision++;

            var result = new ModerationDraftSaveResult(
                ModerationDraftSaveStatus.SUCCEEDED,
                workspace.WorkspaceRevision,
                newRevision,
                IdempotentResultJson: null,
                Replayed: false);
            var resultJson = JsonSerializer.Serialize(
                new DraftSaveIdempotencyPayload(result.WorkspaceRevision, result.MetadataRevision, result.Replayed),
                _idempotencyJsonOptions);

            var record = new JsonMutationIdempotencyRecord
            {
                Id = Guid.NewGuid(),
                ActorUserId = request.ActorUserId,
                OperationKind = request.OperationKind,
                OperationId = request.OperationId,
                RequestDigest = request.RequestDigest,
                ResultJson = resultJson,
                CreatedAt = now
            };
            dbContext.JsonMutationIdempotencyRecords.Add(record);

            try
            {
                await WriteAuditInTransaction(request.AuditEvent, ct);
                await dbContext.SaveChangesAsync(ct);
            }
            catch (DbUpdateException ex) when (IsIdempotencyOperationUniqueViolation(ex))
            {
                outcome = new ModerationDraftSaveResult(ModerationDraftSaveStatus.OPERATION_CONFLICT, 0);
                return;
            }

            outcome = result with { WorkspaceId = workspace.Id, IdempotentResultJson = resultJson };
        }, cancellationToken);
        return outcome;
    }

    public async Task<SellerDeclarationSnapshotDto?> GetOwnerDeclarationSnapshot(
        Guid assetId,
        Guid? assetVersionId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        AssetOwnershipDto? ownership = await assetStore.GetOwnership(assetId, cancellationToken);
        if (ownership is null || ownership.AuthorId != ownerUserId)
        {
            return null;
        }

        AssetDraftWorkspace? workspace = await FindWorkspace(assetId, assetVersionId, cancellationToken);
        if (workspace is null)
        {
            return null;
        }

        MaterialAndDeclaration? heads = await LoadLatestHeads(assetId, workspace.Id, cancellationToken);
        SourceDeclarationPayload? declaration = heads?.Declaration;
        return new SellerDeclarationSnapshotDto(
            assetId,
            workspace.AssetVersionId,
            workspace.Id,
            workspace.WorkspaceRevision,
            workspace.SourceDeclarationHeadRevision,
            declaration,
            IsDeclarationComplete(declaration));
    }

    public async Task<SellerVersionReviewDto?> GetOwnerVersionReview(
        Guid assetVersionId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.AssetVersions
            .AsNoTracking()
            .Where(v => v.Id == assetVersionId)
            .Select(v => new
            {
                v.Id,
                v.AssetId,
                v.VersionNumber,
                v.ProcessingStatus,
                v.ProcessingErrorCode,
                v.ProcessingErrorSummary,
                v.Asset.AuthorId,
                v.Asset.DeletedAt,
                v.Asset.CurrentPublicationSnapshotId
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null || row.AuthorId != ownerUserId)
        {
            return null;
        }

        ModerationSubmissionState moderationState = await GetEffectiveModerationState(assetVersionId, cancellationToken);
        var declarationComplete = await IsVersionDeclarationComplete(row.AssetId, assetVersionId, cancellationToken);

        // Established publication predicate: the version must be the asset's current public
        // offering, backed by a trusted approved snapshot, READY bytes, and a matching hash.
        var publicationEligible = row.DeletedAt is null
            && row.ProcessingStatus == AssetVersionProcessingStatus.READY
            && row.CurrentPublicationSnapshotId is { } currentSnapshotId
            && await PublicationEligibilityQuery.TrustedApprovedSnapshots(dbContext)
                .AnyAsync(
                    s => s.Id == currentSnapshotId
                        && s.AssetId == row.AssetId
                        && s.AssetVersionId == assetVersionId
                        && dbContext.AssetVersions.Any(v =>
                            v.Id == s.AssetVersionId
                            && v.ContentSha256 == s.ContentSha256
                            && v.ProcessingStatus == AssetVersionProcessingStatus.READY),
                    cancellationToken);

        var blocked = new List<string>();
        if (row.ProcessingStatus != AssetVersionProcessingStatus.READY)
        {
            blocked.Add(ErrorCodes.ERR_VERSION_FILE_CHECKS_INCOMPLETE);
        }

        blocked.Add(ErrorCodes.ERR_ANALYSIS_NOT_AVAILABLE);
        if (!declarationComplete)
        {
            blocked.Add(ErrorCodes.ERR_DECLARATION_INCOMPLETE);
        }

        return new SellerVersionReviewDto(
            row.AssetId,
            row.Id,
            row.VersionNumber,
            row.ProcessingStatus,
            row.ProcessingErrorCode,
            row.ProcessingErrorSummary,
            SellerAnalysisAvailability.ANALYSIS_NOT_AVAILABLE,
            moderationState,
            publicationEligible,
            !row.DeletedAt.HasValue,
            false,
            blocked);
    }

    public async Task<VersionAttachResult> AttachUploadedVersionToWorkspace(
        Guid assetId,
        Guid versionId,
        Guid ownerUserId,
        Guid workspaceId,
        long expectedWorkspaceRevision,
        CancellationToken cancellationToken = default)
    {
        Asset? asset = await assetStore.GetForUpdate(assetId, cancellationToken);
        if (asset is null || asset.AuthorId != ownerUserId || asset.DeletedAt.HasValue)
        {
            return new VersionAttachResult(VersionAttachStatus.FORBIDDEN);
        }

        // The client names the exact workspace; identity, ownership, and revision are re-verified
        // under the asset/workspace locks after storage so an edit during streaming cannot bind.
        Guid lockedWorkspaceId = await dbContext.Database
            .SqlQuery<Guid>($"""
                SELECT "Id" AS "Value"
                FROM asset_draft_workspaces
                WHERE "Id" = {workspaceId} AND "AssetId" = {assetId} AND "AssetVersionId" IS NULL
                FOR UPDATE
                """)
            .FirstOrDefaultAsync(cancellationToken);
        if (lockedWorkspaceId == Guid.Empty)
        {
            return new VersionAttachResult(VersionAttachStatus.CONFLICT);
        }

        // Fresh read under the row lock; a tracked instance would carry pre-lock values.
        AssetDraftWorkspace? preUpload = await ReadWorkspaceFreshUnderLock(lockedWorkspaceId, cancellationToken);
        if (preUpload is null)
        {
            return new VersionAttachResult(VersionAttachStatus.CONFLICT);
        }

        // Post-storage recheck: a concurrent declaration/metadata edit must not bind silently.
        if (preUpload.WorkspaceRevision != expectedWorkspaceRevision)
        {
            return new VersionAttachResult(VersionAttachStatus.CONFLICT);
        }

        var versionExists = await dbContext.AssetVersions
            .AsNoTracking()
            .AnyAsync(v => v.Id == versionId && v.AssetId == assetId, cancellationToken);
        if (!versionExists)
        {
            return new VersionAttachResult(VersionAttachStatus.CONFLICT);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        var versionWorkspace = new AssetDraftWorkspace
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            AssetVersionId = versionId,
            WorkspaceVersionScopeKey = versionId,
            ScopeId = Guid.NewGuid(),
            WorkspaceRevision = 1,
            CaseRevision = 1,
            CreatedAt = now
        };

        MaterialAndDeclaration? heads = await LoadLatestHeads(assetId, preUpload.Id, cancellationToken);

        if (heads?.Material is not null)
        {
            versionWorkspace.MaterialMetadataHeadRevision = 1;
            dbContext.AssetMaterialMetadataRevisions.Add(new AssetMaterialMetadataRevision
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                WorkspaceId = versionWorkspace.Id,
                AssetVersionId = versionId,
                WorkspaceVersionScopeKey = versionId,
                Revision = 1,
                AuthorUserId = ownerUserId,
                ContentDigest = heads.MaterialDigest,
                SchemaVersion = SellerDraftLimits.MATERIAL_METADATA_SCHEMA_VERSION,
                PayloadJson = heads.MaterialPayloadJson,
                CreatedAt = now
            });
        }

        if (heads?.Declaration is not null)
        {
            versionWorkspace.SourceDeclarationHeadRevision = 1;
            dbContext.AssetSourceDeclarationRevisions.Add(new AssetSourceDeclarationRevision
            {
                Id = Guid.NewGuid(),
                AssetId = assetId,
                WorkspaceId = versionWorkspace.Id,
                AssetVersionId = versionId,
                WorkspaceVersionScopeKey = versionId,
                Revision = 1,
                AuthorUserId = ownerUserId,
                ContentDigest = heads.DeclarationDigest,
                SchemaVersion = SellerDraftLimits.DECLARATION_SCHEMA_VERSION,
                PayloadJson = heads.DeclarationPayloadJson,
                CreatedAt = now
            });
        }

        // The pre-upload head becomes the starting point for the next version upload.
        preUpload.WorkspaceRevision++;

        dbContext.AssetDraftWorkspaces.Add(versionWorkspace);
        await dbContext.SaveChangesAsync(cancellationToken);

        return new VersionAttachResult(
            VersionAttachStatus.ATTACHED,
            ToSnapshot(versionWorkspace),
            versionWorkspace.MaterialMetadataHeadRevision,
            versionWorkspace.SourceDeclarationHeadRevision);
    }

    public async Task<WithdrawSubmissionResult> WithdrawSubmission(
        WithdrawSubmissionRequest request,
        CancellationToken cancellationToken = default)
    {
        var row = await dbContext.ModerationSubmissions
            .AsNoTracking()
            .Where(s => s.Id == request.SubmissionId)
            .Select(s => new { s.AssetId, s.WorkspaceId })
            .FirstOrDefaultAsync(cancellationToken);
        if (row is null)
        {
            return new WithdrawSubmissionResult(WithdrawSubmissionStatus.NOT_FOUND);
        }

        WithdrawSubmissionResult outcome = null!;
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            outcome = await WithdrawSubmissionUnderLock(request, row.AssetId, row.WorkspaceId, ct);
        }, cancellationToken);
        return outcome;
    }

    private async Task<WithdrawSubmissionResult> WithdrawSubmissionUnderLock(
        WithdrawSubmissionRequest request,
        Guid assetId,
        Guid workspaceId,
        CancellationToken cancellationToken)
    {
        // Canonical lock order shared with saves: Asset → workspace → submission/case.
        Asset? asset = await assetStore.GetForUpdate(assetId, cancellationToken);
        if (asset is null || asset.AuthorId != request.ActorUserId)
        {
            return new WithdrawSubmissionResult(WithdrawSubmissionStatus.FORBIDDEN);
        }

        if (workspaceId != Guid.Empty)
        {
            Guid lockedWorkspaceId = await dbContext.Database
                .SqlQuery<Guid>($"""
                    SELECT "Id" AS "Value"
                    FROM asset_draft_workspaces
                    WHERE "Id" = {workspaceId}
                    FOR UPDATE
                    """)
                .FirstOrDefaultAsync(cancellationToken);
            if (lockedWorkspaceId == Guid.Empty)
            {
                return new WithdrawSubmissionResult(WithdrawSubmissionStatus.NOT_FOUND);
            }
        }

        Guid submissionId = request.SubmissionId;
        Guid lockedSubmissionId = await dbContext.Database
            .SqlQuery<Guid>($"""
                SELECT "Id" AS "Value"
                FROM moderation_submissions
                WHERE "Id" = {submissionId}
                FOR UPDATE
                """)
            .FirstOrDefaultAsync(cancellationToken);
        if (lockedSubmissionId == Guid.Empty)
        {
            return new WithdrawSubmissionResult(WithdrawSubmissionStatus.NOT_FOUND);
        }

        // Re-read under the row lock with a fresh read; tracked state may have moved since the pre-read.
        ModerationSubmission? submission = await ReadSubmissionFreshUnderLock(submissionId, cancellationToken);
        if (submission is null)
        {
            return new WithdrawSubmissionResult(WithdrawSubmissionStatus.NOT_FOUND);
        }

        JsonMutationIdempotencyRecord? existing = await FindIdempotencyRecord(
            request.ActorUserId, ModerationOperationKinds.SUBMISSION_WITHDRAW, request.OperationId, cancellationToken);
        if (existing is not null)
        {
            if (existing.RequestDigest != request.RequestDigest)
            {
                return new WithdrawSubmissionResult(WithdrawSubmissionStatus.OPERATION_CONFLICT);
            }

            WithdrawIdempotencyPayload? replayed = JsonSerializer.Deserialize<WithdrawIdempotencyPayload>(existing.ResultJson, _idempotencyJsonOptions);
            return new WithdrawSubmissionResult(
                WithdrawSubmissionStatus.SUCCEEDED,
                replayed?.CaseRevision,
                existing.ResultJson,
                Replayed: true);
        }

        if (submission.OwnerUserId != request.ActorUserId)
        {
            return new WithdrawSubmissionResult(WithdrawSubmissionStatus.FORBIDDEN);
        }

        var isActive = submission.State is ModerationSubmissionState.SUBMITTED or ModerationSubmissionState.IN_REVIEW;
        if (!isActive)
        {
            return new WithdrawSubmissionResult(WithdrawSubmissionStatus.NOT_ACTIVE, submission.CaseRevision);
        }

        if (submission.CaseRevision != request.ExpectedCaseRevision)
        {
            return new WithdrawSubmissionResult(WithdrawSubmissionStatus.STALE_CASE, submission.CaseRevision);
        }

        DateTimeOffset now = _timeProvider.GetUtcNow();
        submission.State = ModerationSubmissionState.WITHDRAWN;
        submission.WithdrawalReason = request.WithdrawalReason;
        submission.CaseRevision++;

        var history = new ModerationSubmissionHistoryEntry
        {
            Id = Guid.NewGuid(),
            SubmissionId = submission.Id,
            AssetId = submission.AssetId,
            AssetVersionId = submission.AssetVersionId,
            State = ModerationSubmissionState.WITHDRAWN,
            WithdrawalReason = request.WithdrawalReason,
            CaseRevision = submission.CaseRevision,
            ActorUserId = request.ActorUserId,
            Summary = request.WithdrawalReason.ToString(),
            CreatedAt = now
        };
        dbContext.ModerationSubmissionHistoryEntries.Add(history);

        // Fresh read under the locks; a tracked instance would carry a pre-lock CaseRevision.
        AssetDraftWorkspace? workspace = await ReadWorkspaceFreshUnderLock(submission.WorkspaceId, cancellationToken);
        if (workspace is not null)
        {
            workspace.CaseRevision++;
        }

        var payload = new WithdrawIdempotencyPayload(submission.CaseRevision);
        var resultJson = JsonSerializer.Serialize(payload, _idempotencyJsonOptions);
        var record = new JsonMutationIdempotencyRecord
        {
            Id = Guid.NewGuid(),
            ActorUserId = request.ActorUserId,
            OperationKind = ModerationOperationKinds.SUBMISSION_WITHDRAW,
            OperationId = request.OperationId,
            RequestDigest = request.RequestDigest,
            ResultJson = resultJson,
            CreatedAt = now
        };
        dbContext.JsonMutationIdempotencyRecords.Add(record);

        try
        {
            await WriteAuditInTransaction(request.AuditEvent, cancellationToken);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsIdempotencyOperationUniqueViolation(ex))
        {
            return new WithdrawSubmissionResult(WithdrawSubmissionStatus.OPERATION_CONFLICT);
        }

        return new WithdrawSubmissionResult(WithdrawSubmissionStatus.SUCCEEDED, submission.CaseRevision, resultJson);
    }

    /// <summary>Withdraws the single active case bound to this workspace with reason SUPERSEDED; history stays immutable.</summary>
    private async Task SupersedeActiveCase(AssetDraftWorkspace workspace, Guid actorUserId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        // Submission rows are locked after the workspace lock, matching the canonical order
        // Asset → workspace → submission so withdrawal cannot deadlock against saves.
        // State is a string-mapped column; compare against canonical enum member names.
        await dbContext.Database
            .SqlQuery<Guid>($"""
                SELECT "Id" AS "Value"
                FROM moderation_submissions
                WHERE "WorkspaceId" = {workspace.Id}
                  AND "State" IN ({nameof(ModerationSubmissionState.SUBMITTED)}, {nameof(ModerationSubmissionState.IN_REVIEW)})
                FOR UPDATE
                """)
            .ToListAsync(cancellationToken);

        List<ModerationSubmission> activeCases = await dbContext.ModerationSubmissions
            .Where(s => s.WorkspaceId == workspace.Id
                && (s.State == ModerationSubmissionState.SUBMITTED || s.State == ModerationSubmissionState.IN_REVIEW))
            .ToListAsync(cancellationToken);

        foreach (ModerationSubmission submission in activeCases)
        {
            submission.State = ModerationSubmissionState.WITHDRAWN;
            submission.WithdrawalReason = ModerationWithdrawalReason.SUPERSEDED;
            submission.CaseRevision++;

            dbContext.ModerationSubmissionHistoryEntries.Add(new ModerationSubmissionHistoryEntry
            {
                Id = Guid.NewGuid(),
                SubmissionId = submission.Id,
                AssetId = submission.AssetId,
                AssetVersionId = submission.AssetVersionId,
                State = ModerationSubmissionState.WITHDRAWN,
                WithdrawalReason = ModerationWithdrawalReason.SUPERSEDED,
                CaseRevision = submission.CaseRevision,
                ActorUserId = actorUserId,
                Summary = nameof(ModerationWithdrawalReason.SUPERSEDED),
                CreatedAt = now
            });

            workspace.CaseRevision++;
        }
    }

    private async Task<ModerationSubmissionState> GetEffectiveModerationState(Guid assetVersionId, CancellationToken cancellationToken)
    {
        // The latest applicable case determines the state; terminal outcomes (APPROVED,
        // REJECTED, CHANGES_REQUESTED, WITHDRAWN) must not be flattened back to DRAFT.
        ModerationSubmissionState? state = await dbContext.ModerationSubmissions
            .AsNoTracking()
            .Where(s => s.AssetVersionId == assetVersionId)
            .OrderByDescending(s => s.CaseRevision)
            .ThenByDescending(s => s.CreatedAt)
            .Select(s => (ModerationSubmissionState?)s.State)
            .FirstOrDefaultAsync(cancellationToken);

        return state ?? ModerationSubmissionState.DRAFT;
    }

    private async Task<bool> IsVersionDeclarationComplete(Guid assetId, Guid assetVersionId, CancellationToken cancellationToken)
    {
        AssetDraftWorkspace? workspace = await FindWorkspace(assetId, assetVersionId, cancellationToken);
        if (workspace is null)
        {
            return false;
        }

        MaterialAndDeclaration? heads = await LoadLatestHeads(assetId, workspace.Id, cancellationToken);
        return IsDeclarationComplete(heads?.Declaration);
    }

    private async Task<MaterialAndDeclaration?> LoadLatestHeads(Guid assetId, Guid workspaceId, CancellationToken cancellationToken)
    {
        var materialJson = await dbContext.AssetMaterialMetadataRevisions
            .AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId)
            .OrderByDescending(r => r.Revision)
            .Select(r => r.PayloadJson)
            .FirstOrDefaultAsync(cancellationToken);

        var materialDigest = await dbContext.AssetMaterialMetadataRevisions
            .AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId)
            .OrderByDescending(r => r.Revision)
            .Select(r => r.ContentDigest)
            .FirstOrDefaultAsync(cancellationToken);

        var declarationJson = await dbContext.AssetSourceDeclarationRevisions
            .AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId)
            .OrderByDescending(r => r.Revision)
            .Select(r => r.PayloadJson)
            .FirstOrDefaultAsync(cancellationToken);

        var declarationDigest = await dbContext.AssetSourceDeclarationRevisions
            .AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId)
            .OrderByDescending(r => r.Revision)
            .Select(r => r.ContentDigest)
            .FirstOrDefaultAsync(cancellationToken);

        if (materialJson is null && declarationJson is null)
        {
            return null;
        }

        SellerDraftMaterialPayload? material = materialJson is null
            ? null
            : JsonSerializer.Deserialize<SellerDraftMaterialPayload>(materialJson, _idempotencyJsonOptions);
        SourceDeclarationPayload? declaration = declarationJson is null
            ? null
            : JsonSerializer.Deserialize<SourceDeclarationPayload>(declarationJson, _idempotencyJsonOptions);

        return new MaterialAndDeclaration(
            material,
            materialDigest ?? string.Empty,
            materialJson ?? string.Empty,
            declaration,
            declarationDigest ?? string.Empty,
            declarationJson ?? string.Empty);
    }

    private Task<AssetDraftWorkspace?> FindWorkspace(Guid assetId, Guid? assetVersionId, CancellationToken cancellationToken)
    {
        return assetVersionId is null
            ? dbContext.AssetDraftWorkspaces
                .AsNoTracking()
                .SingleOrDefaultAsync(w => w.AssetId == assetId && w.AssetVersionId == null, cancellationToken)
            : dbContext.AssetDraftWorkspaces
                .AsNoTracking()
                .SingleOrDefaultAsync(w => w.AssetId == assetId && w.AssetVersionId == assetVersionId, cancellationToken);
    }

    private Task<JsonMutationIdempotencyRecord?> FindIdempotencyRecord(
        Guid actorUserId,
        string operationKind,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        return dbContext.JsonMutationIdempotencyRecords
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.ActorUserId == actorUserId
                && r.OperationKind == operationKind
                && r.OperationId == operationId,
                cancellationToken);
    }

    private static bool IsDeclarationComplete(SourceDeclarationPayload? declaration)
    {
        if (declaration is null)
        {
            return false;
        }

        if (!declaration.RedistributionAcknowledged
            || string.IsNullOrWhiteSpace(declaration.OwnContributionSummary))
        {
            return false;
        }

        foreach (SourceDeclarationComponent component in declaration.Components)
        {
            if (string.IsNullOrWhiteSpace(component.SourceUrl)
                || string.IsNullOrWhiteSpace(component.License))
            {
                return false;
            }
        }

        return true;
    }

    private static ModerationDraftSaveResult DeserializeDraftSaveResult(string resultJson)
    {
        DraftSaveIdempotencyPayload payload = JsonSerializer.Deserialize<DraftSaveIdempotencyPayload>(resultJson, _idempotencyJsonOptions)
            ?? throw new InvalidOperationException("Draft save idempotency payload is missing.");

        return new ModerationDraftSaveResult(
            ModerationDraftSaveStatus.SUCCEEDED,
            payload.WorkspaceRevision,
            payload.HeadRevision,
            resultJson,
            Replayed: true);
    }

    private sealed record MaterialAndDeclaration(
        SellerDraftMaterialPayload? Material,
        string MaterialDigest,
        string MaterialPayloadJson,
        SourceDeclarationPayload? Declaration,
        string DeclarationDigest,
        string DeclarationPayloadJson);

    private sealed record DraftSaveIdempotencyPayload(long WorkspaceRevision, long? HeadRevision, bool Replayed);

    private sealed record DraftCreateIdempotencyPayload(Guid AssetId, Guid WorkspaceId, long WorkspaceRevision);

    private sealed record WithdrawIdempotencyPayload(long CaseRevision);

    private static string ComputeSha256Hex(string value)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(value);
        return Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
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
