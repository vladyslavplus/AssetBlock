using System.Data;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>
/// Builds one immutable analysis run and its single bound job from persisted state only. Seller
/// supplied data never becomes authority: the tuple is read from the stored workspace, declaration
/// and material revisions, and the exact pinned policy row. Every write happens inside one
/// transaction taken under the Asset -> workspace -> version -> run -> job lock order, so the run
/// and its job either both exist or neither does.
/// </summary>
internal sealed class AnalysisRunEnqueuer(
    ApplicationDbContext dbContext,
    IOptions<CodeAnalysisOptions> analysisOptions,
    IOptions<AssetProcessingOptions> processingOptions,
    ILogger<AnalysisRunEnqueuer> logger) : IAnalysisRunEnqueuer
{
    private static readonly JsonSerializerOptions _schedulingIntentJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task<InitialUploadProofResult> EnqueueForInitialUpload(
        Guid assetId,
        Guid assetVersionId,
        string contentSha256,
        int malwareDefinitionVersion,
        MalwareScanEvidence evidence,
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        // Evidence, READY promotion and the run/job pair (or the durable intent that later
        // produces it) must share one transaction. A caller that already opened one owns it, so
        // only the rollback of a transaction this method started happens here.
        IDbContextTransaction? transaction = await BeginOwnedTransaction(cancellationToken).ConfigureAwait(false);

        try
        {
            InitialUploadProofResult result = await EnqueueForInitialUploadCore(
                    assetId, assetVersionId, contentSha256, malwareDefinitionVersion, evidence, jobId, cancellationToken)
                .ConfigureAwait(false);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            await RollbackOwned(transaction).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<InitialUploadProofResult> EnqueueForInitialUploadCore(
        Guid assetId,
        Guid assetVersionId,
        string contentSha256,
        int malwareDefinitionVersion,
        MalwareScanEvidence evidence,
        Guid jobId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset dbNow = await ReadClock(cancellationToken).ConfigureAwait(false);

        AssetVersion? version = await dbContext.AssetVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == assetVersionId && v.AssetId == assetId, cancellationToken)
            .ConfigureAwait(false);

        // Fail closed: observed proof must describe the exact advertised version. Expected values
        // are never compared to themselves and observed bytes are never defaulted to expected.
        if (version is null
            || version.SafetyRestriction != AssetVersionSafetyRestriction.NONE
            || !string.Equals(version.ContentSha256, contentSha256, StringComparison.Ordinal)
            || !string.Equals(evidence.ContentSha256, version.ContentSha256, StringComparison.Ordinal)
            || !evidence.ScopeComplete
            || evidence.ScannedByteCount != version.ContentLength)
        {
            return new InitialUploadProofResult(false, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, false);
        }

        if (!IsMalwareIdentitySufficient(evidence, dbNow))
        {
            throw new SecurityEvidenceIncompleteException(
                "The observed scanner identity is insufficient for analysis admission.");
        }

        // The archive proof belongs to the archive inspection step, which persists it atomically
        // with its own completion. It is read here, never reconstructed from READY state. A
        // missing archive proof is not valid initial trust: fail closed with nothing persisted.
        List<EvidenceIdentity> archiveRows = await dbContext.Database.SqlQueryRaw<EvidenceIdentity>(
            """SELECT "Id", "ContentSha256" FROM security_evidence_records WHERE "AssetVersionId" = {0} AND "EvidenceKind" = 'ARCHIVE_VALIDATION' AND "SecurityRecheckId" IS NULL LIMIT 1""",
            assetVersionId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (archiveRows.Count == 0
            || !string.Equals(archiveRows[0].ContentSha256, version.ContentSha256, StringComparison.Ordinal))
        {
            return new InitialUploadProofResult(false, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, false);
        }

        Guid malwareId = await EnsureInitialEvidence(
            assetId, assetVersionId, evidence, malwareDefinitionVersion, jobId, dbNow, cancellationToken)
            .ConfigureAwait(false);

        if (malwareId == Guid.Empty)
        {
            return new InitialUploadProofResult(false, Guid.Empty, Guid.Empty, Guid.Empty, Guid.Empty, false);
        }

        // The producing malware job is completed by this same transaction after the proof is
        // verified, so it is the one job the contract may observe as still in flight.
        AnalysisRunEnqueueResult enqueued = await EnqueueLocked(
                assetId, assetVersionId, null, archiveRows[0].Id, malwareId, 1, contentSha256, dbNow,
                completingJobId: jobId, cancellationToken)
            .ConfigureAwait(false);

        // The pinned policy, or an older live run, may be temporarily absent while security
        // processing is genuine. The observed proof then persists together with one durable
        // retryable scheduling intent, so recovery later completes exactly one run/job pair. An
        // inadmissible or superseded proof never becomes current again, so no intent pretends it
        // could: nothing is ever scheduled for it.
        if (enqueued.AnalysisRunId == Guid.Empty)
        {
            if (enqueued.IsRetryable)
            {
                await PersistSchedulingIntent(
                        assetId, assetVersionId, null, archiveRows[0].Id, malwareId, 1, contentSha256, dbNow, cancellationToken)
                    .ConfigureAwait(false);
            }

            logger.LogWarning(
                "Analysis for version {VersionId} was not enqueued ({Rejection}); retryable scheduling intent persisted: {IntentPersisted}.",
                assetVersionId, enqueued.Rejection, enqueued.IsRetryable);
        }

        return new InitialUploadProofResult(
            true, archiveRows[0].Id, malwareId, enqueued.AnalysisRunId, enqueued.AnalysisJobId,
            enqueued.AnalysisRunId != Guid.Empty);
    }

    public async Task PersistSchedulingIntent(
        Guid assetId,
        Guid assetVersionId,
        Guid? securityRecheckId,
        Guid archiveEvidenceId,
        Guid malwareEvidenceId,
        int securityGeneration,
        string contentSha256,
        DateTimeOffset databaseNow,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(
            new AnalysisSchedulingPayload(
                assetId, assetVersionId, archiveEvidenceId, malwareEvidenceId, securityGeneration,
                contentSha256, securityRecheckId),
            _schedulingIntentJson);

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO outbox_messages ("Id", "Type", "Payload", "OccurredAt", "Status", "AttemptCount", "ReplayCount")
            VALUES ({Guid.NewGuid()}, {OutboxMessageTypes.ANALYSIS_SCHEDULING}, {payload}, {databaseNow}, {(int)OutboxMessageStatus.PENDING}, 0, 0)
            """, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Mirrors the recheck admission bar for scanner identity: an unavailable identity is
    /// explicitly insufficient and reported for a bounded retry, while an available one must
    /// carry the full engine/signature identity, coherent observation timestamps and an
    /// unexpired signature. No deployment option may weaken this.
    /// </summary>
    private static bool IsMalwareIdentitySufficient(MalwareScanEvidence evidence, DateTimeOffset dbNow)
    {
        if (!evidence.IdentityAvailable)
        {
            return false;
        }

        return !string.IsNullOrWhiteSpace(evidence.EngineIdentity)
            && !string.IsNullOrWhiteSpace(evidence.EngineVersion)
            && !string.IsNullOrWhiteSpace(evidence.SignatureIdentity)
            && evidence.SignatureObservedAt is not null
            && evidence.SignatureValidUntil is not null
            && evidence.SignatureObservedAt <= dbNow
            && evidence.SignatureObservedAt <= evidence.SignatureValidUntil
            && evidence.SignatureValidUntil > dbNow;
    }

    private async Task<Guid> EnsureInitialEvidence(
        Guid assetId,
        Guid assetVersionId,
        MalwareScanEvidence evidence,
        int definitionVersion,
        Guid jobId,
        DateTimeOffset dbNow,
        CancellationToken cancellationToken)
    {
        var digest = ProductDigests.BuildMalwareEvidenceDigest(
            assetId, assetVersionId, evidence, 1, definitionVersion,
            AssetProcessingDefaults.MALWARE_POLICY_VERSION, dbNow);

        var id = Guid.NewGuid();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO security_evidence_records (
                "Id", "AssetId", "AssetVersionId", "ContentSha256", "SecurityRecheckId", "EvidenceKind",
                "JobId", "Generation", "DefinitionVersion", "Outcome", "SecurityPolicyVersion",
                "EngineIdentity", "EngineVersion", "SignatureIdentity", "SignatureObservedAt", "SignatureValidUntil",
                "IdentityAvailable", "ScannedByteCount", "ScannedEntryCount",
                "ScopeComplete", "CompletedAt", "EvidenceDigest", "CreatedAt"
            ) VALUES (
                {id}, {assetId}, {assetVersionId}, {evidence.ContentSha256},
                NULL, 'MALWARE_SCAN', {jobId}, 1,
                {definitionVersion}, 'CLEAN', {AssetProcessingDefaults.MALWARE_POLICY_VERSION},
                {evidence.EngineIdentity}, {evidence.EngineVersion}, {evidence.SignatureIdentity},
                {evidence.SignatureObservedAt}, {evidence.SignatureValidUntil},
                {evidence.IdentityAvailable}, {evidence.ScannedByteCount}, NULL,
                {evidence.ScopeComplete}, {dbNow}, {digest}, {dbNow}
            )
            ON CONFLICT ("AssetVersionId", "EvidenceKind") WHERE "SecurityRecheckId" IS NULL
            DO NOTHING
            """, cancellationToken).ConfigureAwait(false);

        List<Guid> stored = await dbContext.Database.SqlQueryRaw<Guid>(
            """SELECT "Id" AS "Value" FROM security_evidence_records WHERE "AssetVersionId" = {0} AND "EvidenceKind" = 'MALWARE_SCAN' AND "SecurityRecheckId" IS NULL LIMIT 1""",
            assetVersionId).ToListAsync(cancellationToken).ConfigureAwait(false);

        return stored.Count == 0 ? Guid.Empty : stored[0];
    }

    private sealed class EvidenceIdentity
    {
        public Guid Id { get; init; }
        public string ContentSha256 { get; init; } = "";
    }

    public async Task<AnalysisRunEnqueueResult> Enqueue(
        Guid assetId,
        Guid assetVersionId,
        Guid? securityRecheckId,
        Guid archiveEvidenceId,
        Guid malwareEvidenceId,
        int securityGeneration,
        string contentSha256,
        DateTimeOffset databaseNow,
        CancellationToken cancellationToken = default)
    {
        // Recovery reaches this method with no surrounding transaction, so it owns one. Every
        // other caller already opened the transaction that must also carry its evidence, READY
        // promotion, recheck completion, audit or notification, so that work is never committed
        // without the run/job pair and vice versa.
        IDbContextTransaction? transaction = await BeginOwnedTransaction(cancellationToken).ConfigureAwait(false);

        try
        {
            AnalysisRunEnqueueResult result = await EnqueueLocked(
                    assetId, assetVersionId, securityRecheckId, archiveEvidenceId, malwareEvidenceId,
                    securityGeneration, contentSha256, databaseNow, completingJobId: null, cancellationToken)
                .ConfigureAwait(false);

            if (transaction is not null)
            {
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }

            return result;
        }
        catch
        {
            await RollbackOwned(transaction).ConfigureAwait(false);
            throw;
        }
        finally
        {
            if (transaction is not null)
            {
                await transaction.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<AnalysisRunEnqueueResult> EnqueueLocked(
        Guid assetId,
        Guid assetVersionId,
        Guid? securityRecheckId,
        Guid archiveEvidenceId,
        Guid malwareEvidenceId,
        int securityGeneration,
        string contentSha256,
        DateTimeOffset databaseNow,
        Guid? completingJobId,
        CancellationToken cancellationToken)
    {
        // Deterministic lock order: Asset -> workspace -> version -> run -> job. The Asset lock
        // serialises every enqueue, recovery, deletion and security selection for this asset, so
        // the reads below are fresh persisted state instead of a pre-lock snapshot.
        List<Guid> assets = await dbContext.Database.SqlQueryRaw<Guid>(
            """SELECT "Id" FROM assets WHERE "Id" = {0} AND "DeletedAt" IS NULL FOR UPDATE""",
            assetId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (assets.Count != 1)
        {
            return Rejected(AnalysisEnqueueRejection.NOT_CURRENT, "the asset is missing or deleted");
        }

        await LockWorkspace(assetId, assetVersionId, cancellationToken).ConfigureAwait(false);

        List<Guid> versions = await dbContext.Database.SqlQueryRaw<Guid>(
            """SELECT "Id" FROM asset_versions WHERE "Id" = {0} AND "AssetId" = {1} FOR UPDATE""",
            assetVersionId, assetId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (versions.Count != 1)
        {
            return Rejected(AnalysisEnqueueRejection.NOT_CURRENT, "the advertised version no longer exists");
        }

        AssetVersion? version = await dbContext.AssetVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(v => v.Id == assetVersionId && v.AssetId == assetId, cancellationToken)
            .ConfigureAwait(false);

        if (version is null || version.SafetyRestriction != AssetVersionSafetyRestriction.NONE)
        {
            return Rejected(AnalysisEnqueueRejection.NOT_CURRENT, "the version is quarantined");
        }

        if (!string.Equals(version.ContentSha256, contentSha256, StringComparison.Ordinal))
        {
            return Rejected(AnalysisEnqueueRejection.NOT_CURRENT, "the advertised bytes are no longer current");
        }

        CodeAnalysisPolicyVersion? policy = await ResolvePinnedPolicy(cancellationToken).ConfigureAwait(false);
        if (policy is null)
        {
            logger.LogWarning(
                "No policy {PolicyKey}/{PolicyDigest} is pinned; analysis was not enqueued for version {VersionId}.",
                analysisOptions.Value.PolicyKey,
                analysisOptions.Value.PolicyDigest,
                assetVersionId);
            return Rejected(AnalysisEnqueueRejection.POLICY_UNAVAILABLE, "the pinned policy row is missing");
        }

        // The full current-proof contract runs under the same locks the enqueue itself uses, so
        // expired, superseded or incoherent proof can never schedule a run.
        DateTimeOffset proofNow = await ReadClock(cancellationToken).ConfigureAwait(false);
        SecurityProofVerdict verdict = await CurrentSecurityProof.Verify(
            dbContext,
            new SecurityProofExpectation(
                assetId, assetVersionId, contentSha256, securityRecheckId,
                archiveEvidenceId, malwareEvidenceId, securityGeneration, completingJobId),
            version.ContentLength,
            proofNow,
            cancellationToken).ConfigureAwait(false);

        if (!verdict.Accepted)
        {
            return Rejected(verdict.Rejection, verdict.Reason);
        }

        AssetDraftWorkspace? workspace = await dbContext.AssetDraftWorkspaces
            .AsNoTracking()
            .Where(w => w.AssetId == assetId && w.AssetVersionId == assetVersionId)
            .OrderByDescending(w => w.WorkspaceRevision)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        Guid? declarationRevisionId;
        string declarationDigest;
        IReadOnlyList<DeclaredLicenseProjection> declaredLicenses;

        Guid? materialRevisionId;
        string materialDigest;

        if (workspace is null)
        {
            declarationRevisionId = null;
            declarationDigest = ProductDigests.EmptyProjectionDigest(nameof(AssetSourceDeclarationRevision));
            declaredLicenses = [];
            materialRevisionId = null;
            materialDigest = ProductDigests.EmptyProjectionDigest(nameof(AssetMaterialMetadataRevision));
        }
        else
        {
            AssetSourceDeclarationRevision? declaration = await LoadDeclaration(
                workspace.Id, workspace.SourceDeclarationHeadRevision, cancellationToken);
            AssetMaterialMetadataRevision? material = await LoadMaterial(
                workspace.Id, workspace.MaterialMetadataHeadRevision, cancellationToken);

            declarationRevisionId = declaration?.Id;
            declarationDigest = declaration?.ContentDigest
                ?? ProductDigests.EmptyProjectionDigest(nameof(AssetSourceDeclarationRevision));

            materialRevisionId = material?.Id;
            materialDigest = material?.ContentDigest
                ?? ProductDigests.EmptyProjectionDigest(nameof(AssetMaterialMetadataRevision));

            declaredLicenses = ReadDeclaredLicenses(declaration?.PayloadJson);
        }

        var inputTuple = new AnalysisInputTuple(
            assetId,
            assetVersionId,
            contentSha256,
            policy.PolicyDigest,
            declarationDigest,
            materialDigest,
            version.AnalysisSelectionGeneration,
            securityGeneration,
            archiveEvidenceId,
            malwareEvidenceId);

        // The immutable input identity is resolved before any RunId exists, so a duplicate enqueue
        // resolves to the existing bound run instead of comparing per-run digests that never match.
        CodeAnalysisRun? existing = await FindLiveRunForTuple(inputTuple, cancellationToken).ConfigureAwait(false);

        if (existing is not null)
        {
            return await ResolveLiveRunPair(existing, databaseNow, cancellationToken).ConfigureAwait(false);
        }

        // One live run per selection generation is a schema invariant. An older live run built for
        // a different input tuple has to settle first: a run that was never claimed is superseded
        // explicitly (keeping it as history), while an actively leased one is retried later through
        // the durable scheduling intent instead of being killed under its worker.
        CodeAnalysisRun? olderLive = await dbContext.CodeAnalysisRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.AssetId == assetId
                    && r.AssetVersionId == assetVersionId
                    && r.SelectionGeneration == inputTuple.SelectionGeneration
                    && (r.State == CodeAnalysisRunState.QUEUED || r.State == CodeAnalysisRunState.RUNNING),
                cancellationToken)
            .ConfigureAwait(false);

        if (olderLive is not null)
        {
            if (olderLive.State != CodeAnalysisRunState.QUEUED)
            {
                return Rejected(
                    AnalysisEnqueueRejection.PENDING_LIVE_RUN,
                    "an older analysis run is still executing for this selection generation");
            }

            await SupersedeQueuedRun(olderLive.Id, databaseNow, cancellationToken).ConfigureAwait(false);
        }

        var runId = Guid.NewGuid();
        var inputDigest = ProductDigests.BuildAnalysisInputDigest(
            runId,
            inputTuple,
            declaredLicenses,
            policy.ReferenceSnapshotId is null || policy.ModelKey is null || policy.IndexKey is null
                ? null
                : new ReferenceProjection(policy.ReferenceSnapshotId, policy.ModelKey, policy.IndexKey));
        ReuseSource? reuse = await ResolveReuseSource(inputTuple, declarationDigest, cancellationToken)
            .ConfigureAwait(false);

        // The run is inserted before its job exists and bound once the job row is in the same
        // transaction, so a fault at any point rolls back both instead of leaving an orphan run.
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO code_analysis_runs (
                "Id", "AssetId", "AssetVersionId", "AssetDraftWorkspaceId", "ContentSha256",
                "PolicyVersionId", "PolicyDigest", "DeclarationRevisionId", "DeclarationDigest",
                "MaterialRevisionId", "MaterialDigest", "InputDigest", "SelectionGeneration",
                "JobId", "SecurityRecheckId", "ArchiveEvidenceId", "MalwareEvidenceId", "SecurityGeneration",
                "ServiceKey", "State", "CreatedAt", "UpdatedAt"
            ) VALUES (
                {runId}, {inputTuple.AssetId}, {inputTuple.AssetVersionId}, {workspace?.Id}, {inputTuple.ContentSha256},
                {policy.Id}, {inputTuple.PolicyDigest}, {declarationRevisionId}, {inputTuple.DeclarationDigest},
                {materialRevisionId}, {inputTuple.MaterialDigest}, {inputDigest}, {inputTuple.SelectionGeneration},
                NULL, {securityRecheckId}, {inputTuple.ArchiveEvidenceId}, {inputTuple.MalwareEvidenceId},
                {inputTuple.SecurityGeneration}, {analysisOptions.Value.ServiceKey}, 'QUEUED',
                {databaseNow}, {databaseNow}
            )
            ON CONFLICT DO NOTHING
            """, cancellationToken).ConfigureAwait(false);

        List<Guid> inserted = await dbContext.Database.SqlQueryRaw<Guid>(
            """SELECT "Id" FROM code_analysis_runs WHERE "Id" = {0}""", runId).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (inserted.Count == 0)
        {
            // The loser of a concurrent identical enqueue resolves the winner by the same immutable
            // tuple, never by a digest that already carries this loser's own random run id.
            CodeAnalysisRun? winner = await FindLiveRunForTuple(inputTuple, cancellationToken).ConfigureAwait(false);

            if (winner is null)
            {
                throw new InvalidOperationException(
                    $"The analysis run insert for version {assetVersionId} conflicted without a live winner.");
            }

            return await ResolveLiveRunPair(winner, databaseNow, cancellationToken).ConfigureAwait(false);
        }

        Guid jobId = await CreateBoundJob(
                assetId, assetVersionId, runId, inputDigest, policy.PolicyDigest, inputTuple.ContentSha256,
                inputTuple.ArchiveEvidenceId, inputTuple.MalwareEvidenceId, inputTuple.SecurityGeneration,
                reuse, databaseNow, cancellationToken)
            .ConfigureAwait(false);

        var bound = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE code_analysis_runs
            SET "JobId" = {jobId}, "UpdatedAt" = {databaseNow}
            WHERE "Id" = {runId} AND "JobId" IS NULL
            """, cancellationToken).ConfigureAwait(false);

        if (bound != 1)
        {
            throw new InvalidOperationException($"The analysis run {runId} could not be bound to its job.");
        }

        logger.LogInformation(
            "Enqueued analysis run {RunId} with job {JobId} for version {VersionId}.",
            runId, jobId, assetVersionId);

        return new AnalysisRunEnqueueResult(runId, jobId, IsReplay: false);
    }

    /// <summary>
    /// Resolves one complete coherent run/job pair for a run that already exists, materialising
    /// the missing half when a previous deployment left it unbound. A replay never returns a run
    /// without its job.
    /// </summary>
    private async Task<AnalysisRunEnqueueResult> ResolveLiveRunPair(
        CodeAnalysisRun live,
        DateTimeOffset databaseNow,
        CancellationToken cancellationToken)
    {
        if (live.JobId is { } advertisedJobId)
        {
            List<Guid> linked = await dbContext.Database.SqlQueryRaw<Guid>(
                """SELECT "Id" FROM asset_processing_jobs WHERE "Id" = {0} AND "AnalysisRunId" = {1} LIMIT 1""",
                advertisedJobId, live.Id).ToListAsync(cancellationToken).ConfigureAwait(false);

            if (linked.Count == 1)
            {
                return new AnalysisRunEnqueueResult(live.Id, advertisedJobId, IsReplay: true);
            }

            var relinked = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE asset_processing_jobs
                SET "AnalysisRunId" = {live.Id}, "UpdatedAt" = {databaseNow}
                WHERE "Id" = {advertisedJobId}
                  AND "AnalysisRunId" IS NULL
                  AND "Type" = 'CODE_ANALYSIS'
                  AND "AssetId" = {live.AssetId}
                  AND "AssetVersionId" = {live.AssetVersionId}
                """, cancellationToken).ConfigureAwait(false);

            if (relinked == 1)
            {
                return new AnalysisRunEnqueueResult(live.Id, advertisedJobId, IsReplay: true);
            }

            throw new InvalidOperationException($"The advertised job for analysis run {live.Id} is not bound to it.");
        }

        List<Guid> existingJobs = await dbContext.Database.SqlQueryRaw<Guid>(
            """SELECT "Id" FROM asset_processing_jobs WHERE "AnalysisRunId" = {0} LIMIT 1""", live.Id)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        Guid jobId = existingJobs.Count == 1
            ? existingJobs[0]
            : await CreateBoundJob(
                    live.AssetId, live.AssetVersionId, live.Id, live.InputDigest, live.PolicyDigest,
                    live.ContentSha256, live.ArchiveEvidenceId, live.MalwareEvidenceId, live.SecurityGeneration,
                    reuse: null, databaseNow, cancellationToken)
                .ConfigureAwait(false);

        var bound = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE code_analysis_runs
            SET "JobId" = {jobId}, "UpdatedAt" = {databaseNow}
            WHERE "Id" = {live.Id} AND "JobId" IS NULL
            """, cancellationToken).ConfigureAwait(false);

        if (bound != 1)
        {
            throw new InvalidOperationException($"The analysis run {live.Id} could not be bound to its job.");
        }

        logger.LogInformation("Repaired the unbound analysis run {RunId} with job {JobId}.", live.Id, jobId);
        return new AnalysisRunEnqueueResult(live.Id, jobId, IsReplay: true);
    }

    /// <summary>
    /// Resolves the run whose verified extraction, token and vector work this run may reuse. Reuse is
    /// only ever offered for a declaration-only change: the source run must have completed with an
    /// admitted report for this exact version, content, material metadata, policy and security proof,
    /// and its declaration must actually differ, otherwise there is nothing new to reuse. Price is
    /// not part of any of these identities, so a price change never disturbs reuse.
    /// </summary>
    private async Task<ReuseSource?> ResolveReuseSource(
        AnalysisInputTuple inputTuple,
        string declarationDigest,
        CancellationToken cancellationToken)
    {
        List<ReuseCandidateRow> candidates = await dbContext.Database.SqlQueryRaw<ReuseCandidateRow>(
            """
            SELECT r."Id", r."InputDigest", r."DeclarationDigest", r."MaterialDigest"
            FROM code_analysis_runs AS r
            WHERE r."AssetId" = {0}
              AND r."AssetVersionId" = {1}
              AND r."SelectionGeneration" < {2}
              AND r."Disposition" = 'SELECTED'
              AND r."State" = 'COMPLETED'
              AND r."ContentSha256" = {3}
              AND r."PolicyDigest" = {4}
              AND r."MaterialDigest" = {5}
              AND r."SecurityGeneration" = {6}
              AND r."ArchiveEvidenceId" = {7}
              AND r."MalwareEvidenceId" = {8}
              AND r."DeclarationDigest" <> {9}
              AND EXISTS (
                  SELECT 1
                  FROM code_analysis_report_headers AS h
                  JOIN code_analysis_report_admissions AS a ON a."ReportId" = h."Id"
                  WHERE h."AnalysisRunId" = r."Id"
                    AND h."IsFinalized"
                    AND h."Disposition" = 'SELECTED'
                    AND a."AdmissionKind" = 'SERVER_VALIDATED'
                    AND NOT a."CanAuthorizePublication"
                    AND a."ReportDigest" = h."ReportDigest"
                    AND (
                        SELECT COUNT(*)
                        FROM code_analysis_reuse_artifacts AS ra
                        WHERE ra."SourceReportId" = h."Id"
                          AND ra."SourceRunId" = r."Id"
                          AND ra."Stage" IN ('EXTRACTION', 'TOKEN', 'SEMANTIC')
                    ) = 3
              )
            ORDER BY r."CompletedAt" DESC
            LIMIT 1
            """,
            inputTuple.AssetId,
            inputTuple.AssetVersionId,
            inputTuple.SelectionGeneration,
            inputTuple.ContentSha256,
            inputTuple.PolicyDigest,
            inputTuple.MaterialDigest,
            inputTuple.SecurityGeneration,
            inputTuple.ArchiveEvidenceId,
            inputTuple.MalwareEvidenceId,
            declarationDigest).ToListAsync(cancellationToken).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return null;
        }

        ReuseCandidateRow candidate = candidates[0];
        logger.LogInformation(
            "Analysis run for version {VersionId} may reuse verified extraction, token and vector work from run {ReuseRunId} because only the declaration changed.",
            inputTuple.AssetVersionId,
            candidate.Id);

        return new ReuseSource(candidate.Id, candidate.InputDigest, candidate.MaterialDigest);
    }

    private async Task<Guid> CreateBoundJob(
        Guid assetId,
        Guid assetVersionId,
        Guid runId,
        string inputDigest,
        string policyDigest,
        string contentSha256,
        Guid archiveEvidenceId,
        Guid malwareEvidenceId,
        int securityGeneration,
        ReuseSource? reuse,
        DateTimeOffset databaseNow,
        CancellationToken cancellationToken)
    {
        var jobId = Guid.NewGuid();
        var payload = AssetProcessingSerializer.SerializePayload(
            AssetProcessingJobType.CODE_ANALYSIS,
            new CodeAnalysisPayload(
                runId,
                inputDigest,
                policyDigest,
                contentSha256,
                archiveEvidenceId,
                malwareEvidenceId,
                securityGeneration,
                reuse?.RunId,
                reuse?.InputDigest,
                reuse?.MaterialDigest));

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO asset_processing_jobs (
                "Id", "AssetId", "AssetVersionId", "Type", "DefinitionVersion", "Status", "Stage",
                "AttemptCount", "MaxAttempts", "AvailableAt", "CreatedAt", "UpdatedAt", "Payload", "AnalysisRunId"
            ) VALUES (
                {jobId}, {assetId}, {assetVersionId}, 'CODE_ANALYSIS',
                {analysisOptions.Value.JobDefinitionVersion}, 'QUEUED', 'QUEUED', 0,
                {processingOptions.Value.MaxAttempts}, {databaseNow}, {databaseNow}, {databaseNow},
                CAST({payload} AS jsonb), {runId}
            )
            ON CONFLICT ("AnalysisRunId", "Type", "DefinitionVersion") WHERE "AnalysisRunId" IS NOT NULL
            DO NOTHING
            """, cancellationToken).ConfigureAwait(false);

        List<Guid> stored = await dbContext.Database.SqlQueryRaw<Guid>(
            """SELECT "Id" FROM asset_processing_jobs WHERE "AnalysisRunId" = {0} LIMIT 1""", runId)
            .ToListAsync(cancellationToken).ConfigureAwait(false);

        if (stored.Count == 0)
        {
            throw new InvalidOperationException($"The bound analysis job for run {runId} could not be created.");
        }

        return stored[0];
    }

    /// <summary>
    /// Retires a queued run that a newer input tuple replaces: it keeps its row as history, its
    /// never-claimed job stops consuming attempts, and no capacity slot is involved because an
    /// unclaimed run never reserved one.
    /// </summary>
    private async Task SupersedeQueuedRun(Guid runId, DateTimeOffset databaseNow, CancellationToken cancellationToken)
    {
        var superseded = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE code_analysis_runs
            SET "State" = 'CANCELLED',
                "Disposition" = 'HISTORICAL_SUPERSEDED',
                "ErrorCode" = 'SUPERSEDED_INPUT',
                "ErrorSummary" = 'A newer input tuple superseded this queued analysis run.',
                "CompletedAt" = {databaseNow},
                "UpdatedAt" = {databaseNow}
            WHERE "Id" = {runId} AND "State" = 'QUEUED'
            """, cancellationToken).ConfigureAwait(false);

        if (superseded != 1)
        {
            throw new InvalidOperationException($"Analysis run {runId} was not queued when it had to be superseded.");
        }

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'CANCELLED',
                "Stage" = 'CANCELLED',
                "ErrorCode" = 'SUPERSEDED_INPUT',
                "ErrorSummary" = 'A newer input tuple superseded this queued analysis job.',
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "TransportLeaseToken" = NULL,
                "CompletedAt" = {databaseNow},
                "UpdatedAt" = {databaseNow}
            WHERE "AnalysisRunId" = {runId} AND "Status" IN ('QUEUED', 'RETRY_SCHEDULED')
            """, cancellationToken).ConfigureAwait(false);

        logger.LogInformation("Superseded queued analysis run {RunId} with a newer input tuple.", runId);
    }

    private async Task<IDbContextTransaction?> BeginOwnedTransaction(CancellationToken cancellationToken) =>
        dbContext.Database.CurrentTransaction is not null
            ? null
            : await dbContext.Database
                .BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
                .ConfigureAwait(false);

    private async Task RollbackOwned(IDbContextTransaction? transaction)
    {
        if (transaction is null)
        {
            return;
        }

        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception rollbackFailure)
        {
            // Disposing the transaction still rolls it back; the original failure is rethrown.
            logger.LogWarning(rollbackFailure, "Rollback of an analysis scheduling transaction failed.");
        }
    }

    private Task<DateTimeOffset> ReadClock(CancellationToken cancellationToken) =>
        dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            /*-sql*/ """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

    private async Task LockWorkspace(Guid assetId, Guid assetVersionId, CancellationToken cancellationToken)
    {
        await dbContext.Database.SqlQueryRaw<Guid>(
            """
            SELECT "Id"
            FROM asset_draft_workspaces
            WHERE "AssetId" = {0} AND "AssetVersionId" = {1}
            ORDER BY "WorkspaceRevision" DESC
            LIMIT 1
            FOR UPDATE
            """, assetId, assetVersionId).ToListAsync(cancellationToken).ConfigureAwait(false);
    }

    private Task<CodeAnalysisPolicyVersion?> ResolvePinnedPolicy(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(analysisOptions.Value.PolicyDigest))
        {
            return Task.FromResult<CodeAnalysisPolicyVersion?>(null);
        }

        return dbContext.CodeAnalysisPolicyVersions
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.PolicyKey == analysisOptions.Value.PolicyKey
                    && p.PolicyDigest == analysisOptions.Value.PolicyDigest,
                cancellationToken);
    }

    private Task<CodeAnalysisRun?> FindLiveRunForTuple(AnalysisInputTuple tuple, CancellationToken cancellationToken) =>
        dbContext.CodeAnalysisRuns
            .AsNoTracking()
            .FirstOrDefaultAsync(
                r => r.AssetId == tuple.AssetId
                    && r.AssetVersionId == tuple.AssetVersionId
                    && r.ContentSha256 == tuple.ContentSha256
                    && r.PolicyDigest == tuple.PolicyDigest
                    && r.DeclarationDigest == tuple.DeclarationDigest
                    && r.MaterialDigest == tuple.MaterialDigest
                    && r.SelectionGeneration == tuple.SelectionGeneration
                    && r.SecurityGeneration == tuple.SecurityGeneration
                    && r.ArchiveEvidenceId == tuple.ArchiveEvidenceId
                    && r.MalwareEvidenceId == tuple.MalwareEvidenceId
                    && (r.State == CodeAnalysisRunState.QUEUED || r.State == CodeAnalysisRunState.RUNNING),
                cancellationToken);

    private static AnalysisRunEnqueueResult Rejected(AnalysisEnqueueRejection rejection) =>
        new(Guid.Empty, Guid.Empty, IsReplay: false, rejection);

    private AnalysisRunEnqueueResult Rejected(AnalysisEnqueueRejection rejection, string reason)
    {
        logger.LogWarning("Analysis enqueue for a persisted proof was not admitted: {Reason}.", reason);
        return Rejected(rejection);
    }

    private Task<AssetSourceDeclarationRevision?> LoadDeclaration(
        Guid workspaceId,
        int headRevision,
        CancellationToken cancellationToken)
    {
        if (headRevision <= 0)
        {
            return Task.FromResult<AssetSourceDeclarationRevision?>(null);
        }

        return dbContext.AssetSourceDeclarationRevisions
            .AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId && r.Revision == headRevision)
            .OrderByDescending(r => r.Revision)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private Task<AssetMaterialMetadataRevision?> LoadMaterial(
        Guid workspaceId,
        int headRevision,
        CancellationToken cancellationToken)
    {
        if (headRevision <= 0)
        {
            return Task.FromResult<AssetMaterialMetadataRevision?>(null);
        }

        return dbContext.AssetMaterialMetadataRevisions
            .AsNoTracking()
            .Where(r => r.WorkspaceId == workspaceId && r.Revision == headRevision)
            .OrderByDescending(r => r.Revision)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>
    /// Projects the persisted seller declaration onto the analysis license contract through the shared
    /// projector, so this digest and the metadata the later sends cannot diverge.
    /// </summary>
    private static IReadOnlyList<DeclaredLicenseProjection> ReadDeclaredLicenses(string? payloadJson) =>
        DeclaredLicenseProjector.Read(payloadJson);

    /// <summary>
    /// The previously admitted run whose verified extraction, token and vector work a declaration-only
    /// change may reuse, together with the identities the reuse is verified against.
    /// </summary>
    private sealed record ReuseSource(Guid RunId, string InputDigest, string MaterialDigest);

    private sealed class ReuseCandidateRow
    {
        public Guid Id { get; init; }
        public string InputDigest { get; init; } = "";
        public string DeclarationDigest { get; init; } = "";
        public string MaterialDigest { get; init; } = "";
    }
}

/// <summary>Immutable identity of an analysis input, excluding the run and reservation identities.</summary>
internal sealed record AnalysisInputTuple(
    Guid AssetId,
    Guid AssetVersionId,
    string ContentSha256,
    string PolicyDigest,
    string DeclarationDigest,
    string MaterialDigest,
    int SelectionGeneration,
    int SecurityGeneration,
    Guid ArchiveEvidenceId,
    Guid MalwareEvidenceId);

/// <summary>
/// Operational signal that an observed security proof cannot be admitted (incomplete scope or
/// insufficient scanner identity). Callers roll back and retry; nothing is persisted.
/// </summary>
internal sealed class SecurityEvidenceIncompleteException(string message) : Exception(message);
