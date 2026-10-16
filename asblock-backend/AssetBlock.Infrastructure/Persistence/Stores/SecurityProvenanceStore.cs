using System.Data;
using System.Text.RegularExpressions;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>
/// Dedicated leased finalizers for READY-version security rechecks. Original upload jobs and their
/// results are never rewritten: a recheck records its own generation-bound evidence.
///
/// Every transition takes locks in the global order Asset -> recheck -> job, re-reads the persisted
/// state after the locks, and only then writes.
/// </summary>
internal sealed partial class SecurityProvenanceStore(
    ApplicationDbContext dbContext,
    IOptions<AssetProcessingOptions> processingOptions,
    IAnalysisRunEnqueuer analysisRunEnqueuer,
    ILogger<SecurityProvenanceStore> logger) : ISecurityProvenanceStore
{
    private static readonly Regex _errorCodeRegex = ErrorCodeRegex();

    private sealed record VersionRow
    {
        public Guid IdForLock { get; init; }
        public string ContentSha256 { get; init; } = "";
        public long ContentLength { get; init; }
        public string ProcessingStatus { get; init; } = "";
        public string SafetyRestriction { get; init; } = "";
        public bool AssetDeleted { get; init; }
        public Guid? SelectedCodeAnalysisReportId { get; init; }
    }

    private sealed class RecheckRow
    {
        public Guid Id { get; init; }
        public Guid AssetId { get; init; }
        public Guid AssetVersionId { get; init; }
        public string ContentSha256 { get; init; } = "";
        public int SecurityGeneration { get; init; }
        public int DefinitionVersion { get; init; }
        public string SecurityPolicyVersion { get; init; } = "";
        public string OperationDigest { get; init; } = "";
        public Guid OperationReceiptId { get; init; }
        public Guid? ParentRecheckId { get; init; }
        public Guid? ParentAnalysisRunId { get; init; }
        public Guid? ArchiveEvidenceId { get; init; }
        public string State { get; init; } = "";
        public int? SelectedGeneration { get; init; }
    }

    private sealed class JobRow
    {
        public Guid Id { get; init; }
        public Guid AssetId { get; init; }
        public Guid AssetVersionId { get; init; }
        public string Type { get; init; } = "";
        public string Status { get; init; } = "";
        public Guid? LeaseToken { get; init; }
        public DateTimeOffset? LeaseExpiresAt { get; init; }
        public Guid? SecurityRecheckId { get; init; }
    }

    private sealed class EvidenceRow
    {
        public Guid Id { get; init; }
        public string ContentSha256 { get; init; } = "";
        public string EvidenceKind { get; init; } = "";
        public string Outcome { get; init; } = "";
        public int Generation { get; init; }
        public bool ScopeComplete { get; init; }
        public long ScannedByteCount { get; init; }
        public bool IdentityAvailable { get; init; }
        public DateTimeOffset? SignatureValidUntil { get; init; }
    }

    private sealed record RecheckStartOutcome(
        bool Committed,
        bool IsConflict,
        Guid RecheckId,
        int SecurityGeneration,
        Guid ArchiveJobId);

    private sealed record TransitionContext(
        bool Locked,
        JobRow Job,
        RecheckRow Recheck,
        VersionRow Version,
        DateTimeOffset DbNow);

    public async Task<SecurityRecheckStartResult> StartSecurityRecheck(
        SecurityRecheckStartRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateStartRequest(request);

        await using IDbContextTransaction? tx = dbContext.Database.CurrentTransaction is null
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        await LockAsset(request.AssetId, cancellationToken);
        DateTimeOffset dbNow = await ReadDatabaseClock(cancellationToken);

        VersionRow? version = await ReadVersionFreshUnderLock(request.AssetId, request.AssetVersionId, cancellationToken);
        if (version is null
            || version.AssetDeleted
            || version.ProcessingStatus != nameof(AssetVersionProcessingStatus.READY))
        {
            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken);
            }
            return new SecurityRecheckStartResult(Guid.Empty, 0, Guid.Empty, IsReplay: false);
        }

        // The request may never claim a different hash than the persisted version it targets.
        if (!string.Equals(version.ContentSha256, request.ContentSha256, StringComparison.Ordinal))
        {
            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken);
            }
            return new SecurityRecheckStartResult(Guid.Empty, 0, Guid.Empty, IsReplay: false);
        }

        List<RecheckRow> existingRows = await ReadRechecksForVersion(request.AssetVersionId, cancellationToken);

        // The operation receipt is the stable idempotency identity: the same receipt with a
        // different envelope is a conflict and never resolves to another generation.
        RecheckRow? byReceipt = existingRows.FirstOrDefault(r => r.OperationReceiptId == request.OperationReceiptId);
        if (byReceipt is not null)
        {
            if (!string.Equals(byReceipt.OperationDigest, request.OperationDigest, StringComparison.Ordinal))
            {
                if (tx is not null)
                {
                    await tx.CommitAsync(cancellationToken);
                }
                return new SecurityRecheckStartResult(Guid.Empty, 0, Guid.Empty, IsReplay: false);
            }

            Guid replayedJob = await ResolveOrCreateScopedArchiveJob(byReceipt, dbNow, cancellationToken);
            if (tx is not null)
            {
                await tx.CommitAsync(cancellationToken);
            }
            return new SecurityRecheckStartResult(byReceipt.Id, byReceipt.SecurityGeneration, replayedJob, IsReplay: true);
        }

        // A digest replay under a different receipt is still the same operation while it is live.
        // A terminal generation is history, so it neither replays nor blocks a fresh request, and an
        // explicit linked retry always starts a new generation.
        if (request.ParentRecheckId is null)
        {
            RecheckRow? byDigest = existingRows.FirstOrDefault(r =>
                r.OperationDigest == request.OperationDigest
                && r.State is nameof(SecurityRecheckState.QUEUED)
                    or nameof(SecurityRecheckState.INSPECTING)
                    or nameof(SecurityRecheckState.SCANNING));

            if (byDigest is not null)
            {
                Guid replayedJob = await ResolveOrCreateScopedArchiveJob(byDigest, dbNow, cancellationToken);
                if (tx is not null)
                {
                    await tx.CommitAsync(cancellationToken);
                }
                return new SecurityRecheckStartResult(byDigest.Id, byDigest.SecurityGeneration, replayedJob, IsReplay: true);
            }
        }

        // An explicit retry after an exhausted operational failure creates a linked generation.
        if (request.ParentRecheckId is { } parentId)
        {
            RecheckRow? parent = existingRows.FirstOrDefault(r => r.Id == parentId);
            if (parent is null || parent.State != nameof(SecurityRecheckState.ERROR))
            {
                if (tx is not null)
                {
                    await tx.CommitAsync(cancellationToken);
                }
                return new SecurityRecheckStartResult(Guid.Empty, 0, Guid.Empty, IsReplay: false);
            }
        }
        else
        {
            // Equivalent work that is still active is deduplicated separately from a terminal retry.
            RecheckRow? active = existingRows.FirstOrDefault(r =>
                r.ParentRecheckId is null
                && r.ContentSha256 == request.ContentSha256
                && r.SecurityPolicyVersion == request.SecurityPolicyVersion
                && r.DefinitionVersion == request.DefinitionVersion
                && r.State is nameof(SecurityRecheckState.QUEUED)
                    or nameof(SecurityRecheckState.INSPECTING)
                    or nameof(SecurityRecheckState.SCANNING));

            if (active is not null)
            {
                Guid activeJob = await ResolveOrCreateScopedArchiveJob(active, dbNow, cancellationToken);
                if (tx is not null)
                {
                    await tx.CommitAsync(cancellationToken);
                }
                return new SecurityRecheckStartResult(active.Id, active.SecurityGeneration, activeJob, IsReplay: true);
            }
        }

        var nextGeneration = existingRows.Count == 0
            ? 1
            : existingRows.Max(r => r.SecurityGeneration) + 1;

        var recheckId = Guid.NewGuid();

        // The row is inserted without a selection, so the partial unique index is never transiently
        // violated; the selection is then swapped atomically.
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO security_rechecks (
                "Id", "AssetId", "AssetVersionId", "ContentSha256", "SecurityGeneration",
                "SecurityPolicyVersion", "DefinitionVersion", "OperationDigest", "OperationReceiptId",
                "State", "SelectedGeneration", "ParentRecheckId", "ParentAnalysisRunId", "RequestedAt", "CreatedAt"
            ) VALUES (
                {recheckId}, {request.AssetId}, {request.AssetVersionId}, {request.ContentSha256}, {nextGeneration},
                {request.SecurityPolicyVersion}, {request.DefinitionVersion}, {request.OperationDigest}, {request.OperationReceiptId},
                'QUEUED', NULL, {request.ParentRecheckId}, {request.ParentAnalysisRunId}, {dbNow}, {dbNow}
            )
            """, cancellationToken);

        var swapped = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE security_rechecks AS previous
            SET "SelectedGeneration" = NULL,
                "UpdatedAt" = {dbNow}
            WHERE previous."AssetVersionId" = {request.AssetVersionId}
              AND previous."SelectedGeneration" IS NOT NULL
              AND previous."Id" <> {recheckId}
            """, cancellationToken);

        var selected = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE security_rechecks
            SET "SelectedGeneration" = {nextGeneration},
                "UpdatedAt" = {dbNow}
            WHERE "Id" = {recheckId}
            """, cancellationToken);

        if (selected != 1)
        {
            if (tx is not null)
            {
                await tx.RollbackAsync(cancellationToken);
            }
            return new SecurityRecheckStartResult(Guid.Empty, 0, Guid.Empty, IsReplay: false);
        }

        Guid archiveJobId = await EnqueueScopedJob(
            recheckId,
            request.AssetId,
            request.AssetVersionId,
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            request.DefinitionVersion,
            request.SecurityPolicyVersion,
            dbNow,
            cancellationToken);

        if (tx is not null)
        {
            await tx.CommitAsync(cancellationToken);
        }
        logger.LogInformation(
            "Started security recheck {RecheckId} generation {Generation} for version {VersionId}; superseded {Superseded} selections.",
            recheckId, nextGeneration, request.AssetVersionId, swapped);

        return new SecurityRecheckStartResult(recheckId, nextGeneration, archiveJobId, IsReplay: false);
    }

    public Task<SecurityRecheckTransitionResult> TransitionRecheckArchiveAccepted(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        ArchiveValidationEvidence evidence,
        CancellationToken cancellationToken = default) =>
        RunArchiveTransition(jobId, leaseToken, assetId, assetVersionId, evidence, errorCode: null, safeSummary: null, cancellationToken);

    public Task<SecurityRecheckTransitionResult> TransitionRecheckArchiveUnsafe(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default) =>
        RunArchiveTransition(jobId, leaseToken, assetId, assetVersionId, evidence: null, errorCode, safeSummary, cancellationToken);

    private async Task<SecurityRecheckTransitionResult> RunArchiveTransition(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        ArchiveValidationEvidence? evidence,
        string? errorCode,
        string? safeSummary,
        CancellationToken cancellationToken)
    {
        var accepted = evidence is not null;
        if (!accepted)
        {
            ValidateFailureArguments(errorCode, safeSummary);
        }

        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        TransitionContext context = await LockForTransition(
            jobId, leaseToken, assetId, assetVersionId, AssetProcessingJobType.ARCHIVE_INSPECTION, cancellationToken);

        if (!context.Locked)
        {
            return new SecurityRecheckTransitionResult(Committed: false, IsReplay: false);
        }

        if (context.Job.Status == "SUCCEEDED")
        {
            await tx.CommitAsync(cancellationToken);
            return new SecurityRecheckTransitionResult(Committed: true, IsReplay: true);
        }

        if (context.Recheck.State is not (nameof(SecurityRecheckState.QUEUED) or nameof(SecurityRecheckState.INSPECTING)))
        {
            return new SecurityRecheckTransitionResult(Committed: false, IsReplay: false);
        }

        if (!accepted)
        {
            return await FailRecheck(
                tx, context, nameof(SecurityRecheckState.UNSAFE), AssetVersionSafetyRestriction.UNSAFE_ARCHIVE,
                errorCode!, safeSummary!, cancellationToken);
        }

        // A hash mismatch is a security failure against the advertised bytes, never successful evidence.
        if (!string.Equals(evidence!.ContentSha256, context.Version.ContentSha256, StringComparison.Ordinal))
        {
            return await FailRecheck(
                tx, context, nameof(SecurityRecheckState.UNSAFE), AssetVersionSafetyRestriction.INTEGRITY_MISMATCH,
                ErrorCodes.ARCHIVE_CORRUPT, ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT), cancellationToken);
        }

        var insufficiency = ValidateArchiveEvidence(evidence, context.Version);
        if (insufficiency is not null)
        {
            // Incomplete or unidentified verification is an operational failure, not a quarantine.
            return await FailRecheckOperational(tx, context, ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE, insufficiency, cancellationToken);
        }

        Guid evidenceId = await InsertArchiveEvidence(context, evidence, cancellationToken);

        var advanced = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE security_rechecks
            SET "State" = 'SCANNING',
                "ArchiveEvidenceId" = {evidenceId},
                "UpdatedAt" = {context.DbNow}
            WHERE "Id" = {context.Recheck.Id}
              AND "State" IN ('QUEUED', 'INSPECTING')
              AND "SecurityGeneration" = {context.Recheck.SecurityGeneration}
            """, cancellationToken);

        if (advanced != 1)
        {
            return new SecurityRecheckTransitionResult(Committed: false, IsReplay: false);
        }

        Guid malwareJobId = await EnqueueScopedJob(
            context.Recheck.Id, assetId, assetVersionId, AssetProcessingJobType.MALWARE_SCAN,
            context.Recheck.DefinitionVersion, context.Recheck.SecurityPolicyVersion, context.DbNow, cancellationToken);

        await CompleteScopedJob(context.Job.Id, context.DbNow, cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new SecurityRecheckTransitionResult(Committed: true, IsReplay: false, MalwareJobId: malwareJobId);
    }

    public Task<SecurityRecheckTransitionResult> TransitionRecheckMalwareClean(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        MalwareScanEvidence evidence,
        CancellationToken cancellationToken = default) =>
        RunMalwareTransition(jobId, leaseToken, assetId, assetVersionId, evidence, infected: false, errorCode: null, safeSummary: null, cancellationToken);

    public Task<SecurityRecheckTransitionResult> TransitionRecheckMalwareInfected(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        MalwareScanEvidence? evidence,
        CancellationToken cancellationToken = default)
    {
        ValidateFailureArguments(errorCode, safeSummary);
        return RunMalwareTransition(jobId, leaseToken, assetId, assetVersionId, evidence, infected: true, errorCode, safeSummary, cancellationToken);
    }

    public async Task<SecurityRecheckTransitionResult> TransitionRecheckIntegrityMismatch(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default)
    {
        ValidateFailureArguments(errorCode, safeSummary);

        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        TransitionContext context = await LockForTransition(
            jobId, leaseToken, assetId, assetVersionId, expectedType: null, cancellationToken);

        if (!context.Locked)
        {
            return new SecurityRecheckTransitionResult(Committed: false, IsReplay: false);
        }

        // An already-quarantined generation resolves idempotently without new effects.
        if (context.Recheck.State is nameof(SecurityRecheckState.UNSAFE) or nameof(SecurityRecheckState.INFECTED))
        {
            await tx.CommitAsync(cancellationToken);
            return new SecurityRecheckTransitionResult(Committed: true, IsReplay: true);
        }

        return await FailRecheck(
            tx, context, nameof(SecurityRecheckState.UNSAFE), AssetVersionSafetyRestriction.INTEGRITY_MISMATCH,
            errorCode, safeSummary, cancellationToken);
    }

    private async Task<SecurityRecheckTransitionResult> RunMalwareTransition(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        MalwareScanEvidence? evidence,
        bool infected,
        string? errorCode,
        string? safeSummary,
        CancellationToken cancellationToken)
    {
        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        TransitionContext context = await LockForTransition(
            jobId, leaseToken, assetId, assetVersionId, AssetProcessingJobType.MALWARE_SCAN, cancellationToken);

        if (!context.Locked)
        {
            return new SecurityRecheckTransitionResult(Committed: false, IsReplay: false);
        }

        if (context.Job.Status == "SUCCEEDED" && !infected)
        {
            await tx.CommitAsync(cancellationToken);
            return new SecurityRecheckTransitionResult(Committed: true, IsReplay: true);
        }

        if (context.Recheck.State != nameof(SecurityRecheckState.SCANNING))
        {
            return new SecurityRecheckTransitionResult(Committed: false, IsReplay: false);
        }

        if (infected)
        {
            // A threat verdict over an unverified stream carries no proof, but the verdict itself
            // still quarantines: fail closed toward denial, never toward admission.
            if (evidence is not null
                && !string.Equals(evidence.ContentSha256, context.Version.ContentSha256, StringComparison.Ordinal))
            {
                return await FailRecheck(
                    tx, context, nameof(SecurityRecheckState.INFECTED), AssetVersionSafetyRestriction.INTEGRITY_MISMATCH,
                    ErrorCodes.ARCHIVE_CORRUPT, ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT), cancellationToken);
            }

            if (evidence is not null)
            {
                await InsertMalwareEvidence(context, evidence, SecurityEvidenceOutcome.INFECTED, cancellationToken);
            }

            return await FailRecheck(
                tx, context, nameof(SecurityRecheckState.INFECTED), AssetVersionSafetyRestriction.INFECTED,
                errorCode!, safeSummary!, cancellationToken);
        }

        if (evidence is null)
        {
            throw new ArgumentException("Clean transitions require observed evidence.", nameof(evidence));
        }

        if (!string.Equals(evidence.ContentSha256, context.Version.ContentSha256, StringComparison.Ordinal))
        {
            return await FailRecheck(
                tx, context, nameof(SecurityRecheckState.INFECTED), AssetVersionSafetyRestriction.INTEGRITY_MISMATCH,
                ErrorCodes.ARCHIVE_CORRUPT, ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ARCHIVE_CORRUPT), cancellationToken);
        }

        var insufficiency = await ValidateMalwareEvidence(context, evidence, cancellationToken);
        if (insufficiency is not null)
        {
            return await FailRecheckOperational(tx, context, ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE, insufficiency, cancellationToken);
        }

        Guid malwareEvidenceId = await InsertMalwareEvidence(context, evidence, SecurityEvidenceOutcome.CLEAN, cancellationToken);

        var completed = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE security_rechecks
            SET "State" = 'SUCCEEDED',
                "MalwareEvidenceId" = {malwareEvidenceId},
                "CompletedGeneration" = {context.Recheck.SecurityGeneration},
                "CompletedAt" = {context.DbNow},
                "UpdatedAt" = {context.DbNow}
            WHERE "Id" = {context.Recheck.Id}
              AND "State" = 'SCANNING'
              AND "SecurityGeneration" = {context.Recheck.SecurityGeneration}
            """, cancellationToken);

        if (completed != 1)
        {
            return new SecurityRecheckTransitionResult(Committed: false, IsReplay: false);
        }

        await CompleteScopedJob(context.Job.Id, context.DbNow, cancellationToken);

        // Only the selected generation may admit analysis. An older generation keeps its historical
        // result without selecting or enqueueing anything current.
        if (context.Recheck.SelectedGeneration != context.Recheck.SecurityGeneration)
        {
            await tx.CommitAsync(cancellationToken);
            return new SecurityRecheckTransitionResult(Committed: true, IsReplay: false);
        }

        AnalysisRunEnqueueResult run = await analysisRunEnqueuer.Enqueue(
            assetId, assetVersionId, context.Recheck.Id,
            context.Recheck.ArchiveEvidenceId!.Value, malwareEvidenceId,
            context.Recheck.SecurityGeneration, context.Recheck.ContentSha256, context.DbNow,
            context.Recheck.ParentAnalysisRunId, cancellationToken);

        // A missing pinned policy, or an older analysis run that still owns this selection
        // generation, is a recoverable gap: the completed proof commits together with one durable
        // retryable scheduling intent so recovery later finishes the run/job pair. A proof that is
        // inadmissible under the current contract can never schedule anything, so it commits as a
        // completed recheck with no intent and no run at all.
        if (run.AnalysisRunId == Guid.Empty && run.IsRetryable)
        {
            await analysisRunEnqueuer.PersistSchedulingIntent(
                assetId, assetVersionId, context.Recheck.Id,
                context.Recheck.ArchiveEvidenceId!.Value, malwareEvidenceId,
                context.Recheck.SecurityGeneration, context.Recheck.ContentSha256, context.DbNow,
                context.Recheck.ParentAnalysisRunId, cancellationToken);

            logger.LogWarning(
                "Analysis for recheck {RecheckId} generation {Generation} was not enqueued ({Rejection}); a durable retryable scheduling intent was stored.",
                context.Recheck.Id, context.Recheck.SecurityGeneration, run.Rejection);
        }
        else if (run.AnalysisRunId == Guid.Empty)
        {
            logger.LogWarning(
                "Analysis for recheck {RecheckId} generation {Generation} was refused by the current proof contract ({Rejection}); no run and no scheduling intent were created.",
                context.Recheck.Id, context.Recheck.SecurityGeneration, run.Rejection);
        }

        await tx.CommitAsync(cancellationToken);

        return new SecurityRecheckTransitionResult(
            Committed: true, IsReplay: false, AnalysisRunId: run.AnalysisRunId, AnalysisJobId: run.AnalysisJobId);
    }

    public async Task<SecurityRecheckTransitionResult> TransitionRecheckFailed(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default)
    {
        ValidateFailureArguments(errorCode, safeSummary);

        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        TransitionContext context = await LockForTransition(
            jobId, leaseToken, assetId, assetVersionId, expectedType: null, cancellationToken);

        if (!context.Locked)
        {
            return new SecurityRecheckTransitionResult(Committed: false, IsReplay: false);
        }

        if (context.Job.Status == "FAILED")
        {
            await tx.CommitAsync(cancellationToken);
            return new SecurityRecheckTransitionResult(Committed: true, IsReplay: true);
        }

        return await FailRecheckOperational(tx, context, errorCode, safeSummary, cancellationToken);
    }

    /// <summary>
    /// Locks Asset -> recheck -> job and re-reads every value afterwards, so a completion racing a
    /// newer generation, an edit, a deletion or a lost lease is fenced rather than applied.
    /// </summary>
    private async Task<TransitionContext> LockForTransition(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        AssetProcessingJobType? expectedType,
        CancellationToken cancellationToken)
    {
        // The recheck id is discovered without a lock only to establish the lock order.
        List<Guid?> scopeProbe = await dbContext.Database.SqlQueryRaw<Guid?>(
            """SELECT "SecurityRecheckId" FROM asset_processing_jobs WHERE "Id" = {0}""", jobId).ToListAsync(cancellationToken);

        if (scopeProbe.Count == 0 || scopeProbe[0] is not { } recheckId)
        {
            return new TransitionContext(false, null!, null!, null!, default);
        }

        await LockAsset(assetId, cancellationToken);

        RecheckRow? recheck = await ReadRecheckFreshUnderLock(recheckId, cancellationToken);
        if (recheck is null || recheck.AssetVersionId != assetVersionId)
        {
            return new TransitionContext(false, null!, null!, null!, default);
        }

        List<JobRow> jobs = await dbContext.Database.SqlQueryRaw<JobRow>(
            """
            SELECT "Id", "AssetId", "AssetVersionId", "Type", "Status", "LeaseToken", "LeaseExpiresAt", "SecurityRecheckId"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
            FOR UPDATE
            """, jobId).ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return new TransitionContext(false, null!, null!, null!, default);
        }

        DateTimeOffset dbNow = await ReadDatabaseClock(cancellationToken);
        JobRow job = jobs[0];

        VersionRow? version = await ReadVersionFreshUnderLock(assetId, assetVersionId, cancellationToken);
        if (version is null || version.AssetDeleted)
        {
            return new TransitionContext(false, null!, null!, null!, default);
        }

        // Fresh persisted re-read under the locks, so a newer generation is never overwritten.
        RecheckRow? freshRecheck = await ReadRecheck(recheckId, cancellationToken);
        if (freshRecheck is null)
        {
            return new TransitionContext(false, null!, null!, null!, default);
        }

        var fenced = job.Status == "RUNNING"
            && job.LeaseToken == leaseToken
            && job.LeaseExpiresAt > dbNow
            && job.AssetId == assetId
            && job.AssetVersionId == assetVersionId
            && (expectedType is null || job.Type == expectedType.Value.ToString());

        return new TransitionContext(fenced, job, freshRecheck, version, dbNow);
    }

    private async Task<SecurityRecheckTransitionResult> FailRecheck(
        IDbContextTransaction tx,
        TransitionContext context,
        string terminalState,
        AssetVersionSafetyRestriction restriction,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken)
    {
        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE security_rechecks
            SET "State" = {terminalState},
                "ErrorCode" = {errorCode},
                "ErrorSummary" = {safeSummary},
                "CompletedGeneration" = {context.Recheck.SecurityGeneration},
                "CompletedAt" = {context.DbNow},
                "UpdatedAt" = {context.DbNow}
            WHERE "Id" = {context.Recheck.Id}
              AND "State" NOT IN ('SUCCEEDED', 'UNSAFE', 'INFECTED', 'ERROR', 'CANCELLED')
            """, cancellationToken);

        if (updated != 1)
        {
            return new SecurityRecheckTransitionResult(Committed: false, IsReplay: false);
        }

        // An actual unsafe, infected or integrity result quarantines this exact version. Entitlement,
        // license proof and the encrypted blob are retained; only byte delivery is denied.
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_versions
            SET "SafetyRestriction" = {restriction.ToString()},
                "SafetyRestrictionReason" = {errorCode},
                "SafetyRestrictedAt" = {context.DbNow}
            WHERE "Id" = {context.Version.IdForLock}
              AND "AssetId" = {context.Job.AssetId}
            """, cancellationToken);

        await FailScopedJob(context.Job.Id, context.DbNow, errorCode, safeSummary, cancellationToken);
        await tx.CommitAsync(cancellationToken);

        logger.LogWarning(
            "Security recheck {RecheckId} reached {State} with restriction {Restriction}.",
            context.Recheck.Id, terminalState, restriction);

        return new SecurityRecheckTransitionResult(Committed: true, IsReplay: false);
    }

    private async Task<SecurityRecheckTransitionResult> FailRecheckOperational(
        IDbContextTransaction tx,
        TransitionContext context,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken)
    {
        var bounded = AssetProcessingJobStore.BoundErrorSummary(safeSummary);

        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE security_rechecks
            SET "State" = 'ERROR',
                "ErrorCode" = {errorCode},
                "ErrorSummary" = {bounded},
                "CompletedAt" = {context.DbNow},
                "UpdatedAt" = {context.DbNow}
            WHERE "Id" = {context.Recheck.Id}
              AND "State" NOT IN ('SUCCEEDED', 'UNSAFE', 'INFECTED', 'ERROR', 'CANCELLED')
            """, cancellationToken);

        if (updated != 1)
        {
            return new SecurityRecheckTransitionResult(Committed: false, IsReplay: false);
        }

        // An operational failure never quarantines and never clears an independent restriction.
        await FailScopedJob(context.Job.Id, context.DbNow, errorCode, bounded, cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new SecurityRecheckTransitionResult(Committed: true, IsReplay: false);
    }

    private async Task<Guid> InsertArchiveEvidence(
        TransitionContext context,
        ArchiveValidationEvidence evidence,
        CancellationToken cancellationToken)
    {
        var evidenceId = Guid.NewGuid();
        var digest = ProductDigests.BuildArchiveEvidenceDigest(
            context.Job.AssetId, context.Version.IdForLock, evidence, context.Recheck.SecurityGeneration,
            context.Recheck.DefinitionVersion, context.Recheck.SecurityPolicyVersion, context.DbNow);

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO security_evidence_records (
                "Id", "AssetId", "AssetVersionId", "ContentSha256", "SecurityRecheckId", "EvidenceKind",
                "JobId", "Generation", "DefinitionVersion", "Outcome", "SecurityPolicyVersion",
                "ImplementationIdentity", "IdentityAvailable", "ScannedByteCount", "ScannedEntryCount",
                "ScopeComplete", "CompletedAt", "EvidenceDigest", "CreatedAt"
            ) VALUES (
                {evidenceId}, {context.Job.AssetId}, {context.Job.AssetVersionId}, {evidence.ContentSha256},
                {context.Recheck.Id}, 'ARCHIVE_VALIDATION', {context.Job.Id}, {context.Recheck.SecurityGeneration},
                {context.Recheck.DefinitionVersion}, 'VALID', {context.Recheck.SecurityPolicyVersion},
                {evidence.ImplementationIdentity}, false, {evidence.ScannedByteCount}, {evidence.FileCount},
                {evidence.ScopeComplete}, {context.DbNow}, {digest}, {context.DbNow}
            )
            ON CONFLICT ("SecurityRecheckId", "EvidenceKind") WHERE "SecurityRecheckId" IS NOT NULL
            DO NOTHING
            """, cancellationToken);

        List<Guid> stored = await ReadEvidenceId(context.Recheck.Id, SecurityEvidenceKind.ARCHIVE_VALIDATION, cancellationToken);
        return stored.Count == 0 ? evidenceId : stored[0];
    }

    private async Task<Guid> InsertMalwareEvidence(
        TransitionContext context,
        MalwareScanEvidence evidence,
        SecurityEvidenceOutcome outcome,
        CancellationToken cancellationToken)
    {
        var evidenceId = Guid.NewGuid();
        var digest = ProductDigests.BuildMalwareEvidenceDigest(
            context.Job.AssetId, context.Version.IdForLock, evidence, context.Recheck.SecurityGeneration,
            context.Recheck.DefinitionVersion, context.Recheck.SecurityPolicyVersion, context.DbNow);

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO security_evidence_records (
                "Id", "AssetId", "AssetVersionId", "ContentSha256", "SecurityRecheckId", "EvidenceKind",
                "JobId", "Generation", "DefinitionVersion", "Outcome", "SecurityPolicyVersion",
                "EngineIdentity", "EngineVersion", "SignatureIdentity", "SignatureObservedAt", "SignatureValidUntil",
                "IdentityAvailable", "ScannedByteCount", "ScopeComplete", "CompletedAt", "EvidenceDigest", "CreatedAt"
            ) VALUES (
                {evidenceId}, {context.Job.AssetId}, {context.Job.AssetVersionId}, {evidence.ContentSha256},
                {context.Recheck.Id}, 'MALWARE_SCAN', {context.Job.Id}, {context.Recheck.SecurityGeneration},
                {context.Recheck.DefinitionVersion}, {outcome.ToString()}, {context.Recheck.SecurityPolicyVersion},
                {evidence.EngineIdentity}, {evidence.EngineVersion}, {evidence.SignatureIdentity},
                {evidence.SignatureObservedAt}, {evidence.SignatureValidUntil},
                {evidence.IdentityAvailable}, {evidence.ScannedByteCount}, {evidence.ScopeComplete},
                {context.DbNow}, {digest}, {context.DbNow}
            )
            ON CONFLICT ("SecurityRecheckId", "EvidenceKind") WHERE "SecurityRecheckId" IS NOT NULL
            DO NOTHING
            """, cancellationToken);

        List<Guid> stored = await ReadEvidenceId(context.Recheck.Id, SecurityEvidenceKind.MALWARE_SCAN, cancellationToken);
        return stored.Count == 0 ? evidenceId : stored[0];
    }

    /// <summary>
    /// Returns a safe reason when the archive observation cannot be admitted as security evidence.
    /// A complete, hash-bound, identified pass over the whole declared byte scope is required.
    /// </summary>
    private static string? ValidateArchiveEvidence(ArchiveValidationEvidence evidence, VersionRow version)
    {
        if (!evidence.ScopeComplete)
        {
            return ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE);
        }

        if (evidence.ScannedByteCount != version.ContentLength)
        {
            return ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE);
        }

        if (string.IsNullOrWhiteSpace(evidence.ImplementationIdentity))
        {
            return ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE);
        }

        return null;
    }

    /// <summary>
    /// Returns a safe reason when the malware observation, or the archive evidence it depends on,
    /// cannot be admitted as security evidence for this exact generation.
    /// </summary>
    private async Task<string?> ValidateMalwareEvidence(
        TransitionContext context,
        MalwareScanEvidence evidence,
        CancellationToken cancellationToken)
    {
        if (!evidence.ScopeComplete || evidence.ScannedByteCount != context.Version.ContentLength)
        {
            return ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE);
        }

        // The observed scanner identity is mandatory here exactly as in the shared current-proof
        // contract: an unavailable or incoherent identity fails the recheck operationally and is
        // never admitted as proof, and no deployment option may weaken it.
        if (!evidence.IdentityAvailable
            || string.IsNullOrWhiteSpace(evidence.EngineIdentity)
            || string.IsNullOrWhiteSpace(evidence.EngineVersion)
            || string.IsNullOrWhiteSpace(evidence.SignatureIdentity)
            || evidence.SignatureObservedAt is null
            || evidence.SignatureValidUntil is null
            || evidence.SignatureObservedAt > context.DbNow
            || evidence.SignatureObservedAt > evidence.SignatureValidUntil)
        {
            return ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE);
        }

        if (evidence.SignatureValidUntil <= context.DbNow)
        {
            return ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SECURITY_EVIDENCE_SIGNATURE_EXPIRED);
        }

        if (context.Recheck.ArchiveEvidenceId is not { } archiveEvidenceId)
        {
            return ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE);
        }

        List<EvidenceRow> archive = await dbContext.Database.SqlQueryRaw<EvidenceRow>(
            """
            SELECT "Id", "ContentSha256", "EvidenceKind"::text AS "EvidenceKind", "Outcome"::text AS "Outcome",
                   "Generation", "ScopeComplete", "ScannedByteCount", "IdentityAvailable", "SignatureValidUntil"
            FROM security_evidence_records
            WHERE "Id" = {0}
            """, archiveEvidenceId).ToListAsync(cancellationToken);

        if (archive.Count == 0)
        {
            return ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE);
        }

        EvidenceRow archiveRow = archive[0];
        var archiveIsAdmissible = archiveRow.EvidenceKind == nameof(SecurityEvidenceKind.ARCHIVE_VALIDATION)
            && archiveRow.Outcome == nameof(SecurityEvidenceOutcome.VALID)
            && archiveRow.ScopeComplete
            && archiveRow.Generation == context.Recheck.SecurityGeneration
            && string.Equals(archiveRow.ContentSha256, context.Version.ContentSha256, StringComparison.Ordinal);

        return archiveIsAdmissible
            ? null
            : ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SECURITY_EVIDENCE_INCOMPLETE);
    }

    private async Task<Guid> EnqueueScopedJob(
        Guid recheckId,
        Guid assetId,
        Guid assetVersionId,
        AssetProcessingJobType type,
        int definitionVersion,
        string? securityPolicyVersion,
        DateTimeOffset dbNow,
        CancellationToken cancellationToken)
    {
        var jobId = Guid.NewGuid();

        // The scoped scanner job carries exactly the security policy the approved recheck pinned,
        // so the producing job and the proof it records can never claim different policies.
        var payload = type == AssetProcessingJobType.ARCHIVE_INSPECTION
            ? AssetProcessingSerializer.SerializePayload(type, new ArchiveInspectionPayload())
            : AssetProcessingSerializer.SerializePayload(
                type, new MalwareScanPayload(securityPolicyVersion ?? AssetProcessingDefaults.MALWARE_POLICY_VERSION));

        var maxAttempts = processingOptions.Value.MaxAttempts;

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO asset_processing_jobs (
                "Id", "AssetId", "AssetVersionId", "Type", "DefinitionVersion", "Status", "Stage",
                "AttemptCount", "MaxAttempts", "AvailableAt", "CreatedAt", "UpdatedAt", "Payload", "SecurityRecheckId"
            ) VALUES (
                {jobId}, {assetId}, {assetVersionId}, {type.ToString()}, {definitionVersion}, 'QUEUED', 'QUEUED',
                0, {maxAttempts}, {dbNow}, {dbNow}, {dbNow}, CAST({payload} AS jsonb), {recheckId}
            )
            ON CONFLICT ("SecurityRecheckId", "Type", "DefinitionVersion") WHERE "SecurityRecheckId" IS NOT NULL
            DO NOTHING
            """, cancellationToken);

        List<Guid> stored = await dbContext.Database.SqlQueryRaw<Guid>(
            """
            SELECT "Id"
            FROM asset_processing_jobs
            WHERE "SecurityRecheckId" = {0} AND "Type" = {1} AND "DefinitionVersion" = {2}
            LIMIT 1
            """, recheckId, type.ToString(), definitionVersion).ToListAsync(cancellationToken);

        return stored.Count == 0 ? jobId : stored[0];
    }

    private async Task<Guid> ResolveOrCreateScopedArchiveJob(RecheckRow recheck, DateTimeOffset dbNow, CancellationToken cancellationToken)
    {
        List<Guid> existing = await dbContext.Database.SqlQueryRaw<Guid>(
            """
            SELECT "Id"
            FROM asset_processing_jobs
            WHERE "SecurityRecheckId" = {0} AND "Type" = 'ARCHIVE_INSPECTION' AND "DefinitionVersion" = {1}
            LIMIT 1
            """, recheck.Id, recheck.DefinitionVersion).ToListAsync(cancellationToken);

        return existing.Count > 0
            ? existing[0]
            : await EnqueueScopedJob(
                recheck.Id, recheck.AssetId, recheck.AssetVersionId, AssetProcessingJobType.ARCHIVE_INSPECTION,
                recheck.DefinitionVersion, recheck.SecurityPolicyVersion, dbNow, cancellationToken);
    }

    private async Task<List<Guid>> ReadEvidenceId(
        Guid recheckId,
        SecurityEvidenceKind kind,
        CancellationToken cancellationToken) =>
        await dbContext.Database.SqlQueryRaw<Guid>(
            """
            SELECT "Id"
            FROM security_evidence_records
            WHERE "SecurityRecheckId" = {0} AND "EvidenceKind" = {1}
            LIMIT 1
            """, recheckId, kind.ToString()).ToListAsync(cancellationToken);

    private async Task LockAsset(Guid assetId, CancellationToken cancellationToken)
    {
        List<Guid> locked = await dbContext.Database.SqlQueryRaw<Guid>(
            """SELECT "Id" FROM assets WHERE "Id" = {0} FOR UPDATE""", assetId).ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            throw new InvalidOperationException("Asset row disappeared while a security recheck held its lock.");
        }
    }

    private Task<DateTimeOffset> ReadDatabaseClock(CancellationToken cancellationToken) =>
        dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

    private async Task<VersionRow?> ReadVersionFreshUnderLock(Guid assetId, Guid assetVersionId, CancellationToken cancellationToken)
    {
        List<VersionRow> rows = await dbContext.Database.SqlQueryRaw<VersionRow>(
            """
            SELECT v."Id" AS "IdForLock",
                   v."ContentSha256",
                   v."ContentLength",
                   v."ProcessingStatus"::text AS "ProcessingStatus",
                   v."SafetyRestriction"::text AS "SafetyRestriction",
                   (a."DeletedAt" IS NOT NULL) AS "AssetDeleted",
                   v."SelectedCodeAnalysisReportId"
            FROM asset_versions v
            JOIN assets a ON a."Id" = v."AssetId"
            WHERE v."Id" = {0} AND v."AssetId" = {1}
            """, assetVersionId, assetId).ToListAsync(cancellationToken);

        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<RecheckRow?> ReadRecheckFreshUnderLock(Guid recheckId, CancellationToken cancellationToken)
    {
        List<RecheckRow> rows = await dbContext.Database.SqlQueryRaw<RecheckRow>(
            RECHECK_SELECT + " WHERE \"Id\" = {0} FOR UPDATE", recheckId).ToListAsync(cancellationToken);

        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<RecheckRow?> ReadRecheck(Guid recheckId, CancellationToken cancellationToken)
    {
        List<RecheckRow> rows = await dbContext.Database.SqlQueryRaw<RecheckRow>(
            RECHECK_SELECT + " WHERE \"Id\" = {0}", recheckId).ToListAsync(cancellationToken);

        return rows.Count == 0 ? null : rows[0];
    }

    private async Task<List<RecheckRow>> ReadRechecksForVersion(Guid assetVersionId, CancellationToken cancellationToken) =>
        await dbContext.Database.SqlQueryRaw<RecheckRow>(
            RECHECK_SELECT + " WHERE \"AssetVersionId\" = {0} ORDER BY \"SecurityGeneration\" ", assetVersionId)
            .ToListAsync(cancellationToken);

    private Task CompleteScopedJob(Guid jobId, DateTimeOffset dbNow, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'SUCCEEDED',
                "Stage" = 'SUCCEEDED',
                "CompletedAt" = {dbNow},
                "UpdatedAt" = {dbNow},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL
            WHERE "Id" = {jobId} AND "Status" = 'RUNNING'
            """, cancellationToken);

    private Task FailScopedJob(Guid jobId, DateTimeOffset dbNow, string errorCode, string safeSummary, CancellationToken cancellationToken) =>
        dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'FAILED',
                "Stage" = 'FAILED',
                "CompletedAt" = {dbNow},
                "UpdatedAt" = {dbNow},
                "ErrorCode" = {errorCode},
                "ErrorSummary" = {safeSummary},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL
            WHERE "Id" = {jobId} AND "Status" = 'RUNNING'
            """, cancellationToken);

    private static void ValidateFailureArguments(string? errorCode, string? safeSummary)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || !_errorCodeRegex.IsMatch(errorCode))
        {
            throw new ArgumentException("Error code must match ^[A-Z0-9_]{1,64}$.", nameof(errorCode));
        }

        if (string.IsNullOrWhiteSpace(safeSummary))
        {
            throw new ArgumentException("Safe summary must not be null or whitespace.", nameof(safeSummary));
        }
    }

    private static void ValidateStartRequest(SecurityRecheckStartRequest request)
    {
        if (request.AssetId == Guid.Empty || request.AssetVersionId == Guid.Empty)
        {
            throw new ArgumentException("Asset and version ids must not be empty.", nameof(request));
        }

        // A policy version is an identifier, not an error code: it is lowercase and may contain dots,
        // dashes and underscores, so it must not be matched against the uppercase error-code shape.
        if (request.ContentSha256.Length != 64 || !PolicyVersionRegex().IsMatch(request.SecurityPolicyVersion))
        {
            throw new ArgumentException("Content hash or security policy version is invalid.", nameof(request));
        }

        if (request.DefinitionVersion <= 0 || request.OperationDigest.Length != 64)
        {
            throw new ArgumentException("Definition version and operation digest are invalid.", nameof(request));
        }
    }

    private const string RECHECK_SELECT = """
        SELECT "Id", "AssetId", "AssetVersionId", "ContentSha256", "SecurityGeneration",
               "DefinitionVersion", "SecurityPolicyVersion", "OperationDigest", "OperationReceiptId",
                "ParentRecheckId", "ParentAnalysisRunId", "ArchiveEvidenceId", "State"::text AS "State", "SelectedGeneration"
        FROM security_rechecks
        """;

    [GeneratedRegex("^[A-Z0-9_]{1,64}$", RegexOptions.Compiled)]
    private static partial Regex ErrorCodeRegex();

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._\\-]{0,63}$", RegexOptions.Compiled)]
    private static partial Regex PolicyVersionRegex();
}
