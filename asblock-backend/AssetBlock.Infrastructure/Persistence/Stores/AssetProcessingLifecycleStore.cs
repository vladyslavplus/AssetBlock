using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Persistence.Stores;

public sealed partial class AssetProcessingLifecycleStore(
    ApplicationDbContext dbContext,
    IOptions<AssetProcessingOptions> options,
    IAnalysisRunEnqueuer? analysisRunEnqueuer = null,
    IOptions<CodeAnalysisOptions>? analysisOptions = null)
    : IAssetProcessingLifecycleStore
{
    /// <summary>
    /// Production analysis-enabled uploads must carry observed proof: the registered path always
    /// wires the enqueuer, so a proof-less call there fails closed instead of committing a bare
    /// READY. Explicitly legacy/test wiring without an enqueuer keeps the historical behavior.
    /// </summary>
    private bool RequiresObservedProof() =>
        analysisRunEnqueuer is not null && analysisOptions?.Value.Enabled == true;
    private static readonly Regex _errorCodeRegex = MyRegex();
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private sealed class AssetOwnerRow
    {
        public Guid AuthorId { get; init; }
        public string Title { get; init; } = "";
    }

    private sealed class JobValidationRecord
    {
        public Guid Id { get; init; }
        public Guid AssetId { get; init; }
        public Guid AssetVersionId { get; init; }
        public string Type { get; init; } = "";
        public string Status { get; init; } = "";
        public Guid? LeaseToken { get; init; }
        public DateTimeOffset? LeaseExpiresAt { get; init; }
    }

    private sealed class VersionStatusRecord
    {
        public string ContentSha256 { get; init; } = "";
        public long ContentLength { get; init; }
        public string ProcessingStatus { get; init; } = "";
        public string? ProcessingErrorCode { get; init; }
        public string? SafetyRestriction { get; init; }
        public Guid? SelectedCodeAnalysisReportId { get; init; }
    }

    private sealed class ExpiredSecurityJobCandidate
    {
        public Guid Id { get; init; }
        public Guid AssetId { get; init; }
        public Guid AssetVersionId { get; init; }
        public string Type { get; init; } = "";
        public Guid? SecurityRecheckId { get; init; }
    }

    /// <summary>
    /// Takes the asset row lock that every security transition orders behind.
    /// All security success, failure and recovery paths must acquire this before any job,
    /// recheck or run lock so the global order stays Asset -> workspace -> submission -> recheck -> job.
    /// </summary>
    private async Task LockAssetForSecurityTransition(Guid assetId, CancellationToken cancellationToken)
    {
        List<Guid> locked = await dbContext.Database.SqlQueryRaw<Guid>(
            """
            SELECT "Id"
            FROM assets
            WHERE "Id" = {0}
            FOR UPDATE
            """, assetId).ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            throw new InvalidOperationException("Asset row disappeared while a security transition held its lock.");
        }
    }

    /// <summary>
    /// Re-reads version state after the locks are held. Persisted state is authoritative even for
    /// rows already tracked by the change tracker.
    /// </summary>
    private async Task<VersionStatusRecord?> ReadVersionStatusFreshUnderLock(
        Guid assetId,
        Guid assetVersionId,
        CancellationToken cancellationToken)
    {
        List<VersionStatusRecord> rows = await dbContext.Database.SqlQueryRaw<VersionStatusRecord>(
            """
            SELECT v."ContentSha256",
                   v."ContentLength",
                   v."ProcessingStatus",
                   v."ProcessingErrorCode",
                   v."SafetyRestriction"::text AS "SafetyRestriction",
                   v."SelectedCodeAnalysisReportId"
            FROM asset_versions v
            WHERE v."Id" = {0} AND v."AssetId" = {1}
            """, assetVersionId, assetId).ToListAsync(cancellationToken);

        return rows.Count == 0 ? null : rows[0];
    }

    public async Task<bool> TransitionArchiveInspectionAccepted(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        ArchiveInspectionResult result,
        BoundedArchiveAnalysisRecord analysis,
        ArchiveValidationEvidence? observedEvidence = null,
        int? observedDefinitionVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(analysis);

        if (analysis.ReadmeContent is { Length: > 0 })
        {
            var readmeBytes = Encoding.UTF8.GetByteCount(analysis.ReadmeContent);
            if (readmeBytes > 16384)
            {
                throw new ArgumentException($"ReadmeContent ({readmeBytes} bytes) exceeds 16384 byte limit.", nameof(analysis));
            }
        }

        string? manifestJson = null;
        if (analysis.ManifestMetadata is not null)
        {
            manifestJson = ArchiveAnalysisSerializer.SerializeManifestMetadata(analysis.ManifestMetadata);
        }

        var serializedResult = AssetProcessingSerializer.SerializeResult(AssetProcessingJobType.ARCHIVE_INSPECTION, result);

        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Lock the asset first; every other security path uses this same order.
        await LockAssetForSecurityTransition(assetId, cancellationToken);

        // 2. Lock job row and retrieve state
        List<JobValidationRecord> jobs = await dbContext.Database.SqlQueryRaw<JobValidationRecord>(
            """
            SELECT "Id", "AssetId", "AssetVersionId", "Type", "Status", "LeaseToken", "LeaseExpiresAt"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
            FOR UPDATE
            """, jobId).ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return false;
        }

        JobValidationRecord job = jobs[0];

        // 3. Capture DB clock timestamp once after lock acquisition
        DateTimeOffset dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

        // 4. Revalidate fencing & job target
        if (job is { Status: "SUCCEEDED", Type: nameof(AssetProcessingJobType.ARCHIVE_INSPECTION) }
            && job.AssetId == assetId
            && job.AssetVersionId == assetVersionId)
        {
            // Idempotent retry: already succeeded for the same target
            await tx.CommitAsync(cancellationToken);
            return true;
        }

        if (job.Status != "RUNNING"
            || job.LeaseToken != leaseToken
            || job.LeaseExpiresAt <= dbNow
            || job.Type != nameof(AssetProcessingJobType.ARCHIVE_INSPECTION)
            || job.AssetId != assetId
            || job.AssetVersionId != assetVersionId)
        {
            return false;
        }

        // 5. Re-read the persisted version state under the asset lock.
        VersionStatusRecord? freshVersion = await ReadVersionStatusFreshUnderLock(assetId, assetVersionId, cancellationToken);
        if (freshVersion is null)
        {
            return false;
        }

        if (observedEvidence is null && RequiresObservedProof())
        {
            await tx.RollbackAsync(cancellationToken);
            return false;
        }

        // 5b. Persist the observed archive proof atomically with this completion when the caller
        // verified it. The proof must describe the exact advertised bytes; anything else fails
        // closed with no transition and no proof row.
        if (observedEvidence is not null && observedDefinitionVersion is { } archiveDefinitionVersion)
        {
            if (!IsObservedArchiveSufficient(observedEvidence, freshVersion))
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }

            await InsertInitialArchiveEvidence(
                assetId, assetVersionId, jobId, observedEvidence, archiveDefinitionVersion, dbNow, cancellationToken);
        }

        // 6. Upsert archive analysis record
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO asset_archive_analyses (
                "AssetVersionId", "FileCount", "TotalExpandedBytes", "ReadmeContent", "ManifestMetadata", "CreatedAt", "UpdatedAt"
            ) VALUES (
                {assetVersionId}, {analysis.FileCount}, {analysis.TotalExpandedBytes}, {analysis.ReadmeContent}, CAST({manifestJson} AS jsonb), {dbNow}, {dbNow}
            )
            ON CONFLICT ("AssetVersionId") DO UPDATE SET
                "FileCount" = EXCLUDED."FileCount",
                "TotalExpandedBytes" = EXCLUDED."TotalExpandedBytes",
                "ReadmeContent" = EXCLUDED."ReadmeContent",
                "ManifestMetadata" = EXCLUDED."ManifestMetadata",
                "UpdatedAt" = EXCLUDED."UpdatedAt"
            """, cancellationToken);

        // 7. Update asset version processing status — only from PENDING_INSPECTION
        var versionRowsUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_versions
            SET "ProcessingStatus" = 'PENDING_MALWARE_SCAN',
                "ProcessingErrorCode" = NULL,
                "ProcessingErrorSummary" = NULL,
                "ProcessingUpdatedAt" = {dbNow}
            WHERE "Id" = {assetVersionId}
              AND "AssetId" = {assetId}
              AND "ProcessingStatus" = 'PENDING_INSPECTION'
            """, cancellationToken);

        if (versionRowsUpdated == 0)
        {
            // Version is not in the expected PENDING_INSPECTION state (stale or already transitioned)
            await tx.RollbackAsync(cancellationToken);
            return false;
        }

        // 8. Enqueue exactly one MALWARE_SCAN job idempotently
        var nextJobId = Guid.NewGuid();
        var malwarePayload = AssetProcessingSerializer.SerializePayload(
            AssetProcessingJobType.MALWARE_SCAN,
            new MalwareScanPayload(AssetProcessingDefaults.MALWARE_POLICY_VERSION));
        var maxAttempts = options.Value.MaxAttempts;

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO asset_processing_jobs (
                "Id", "AssetId", "AssetVersionId", "Type", "DefinitionVersion", "Status", "Stage",
                "AttemptCount", "MaxAttempts", "AvailableAt", "CreatedAt", "UpdatedAt", "Payload"
            ) VALUES (
                {nextJobId}, {assetId}, {assetVersionId}, 'MALWARE_SCAN', 1, 'QUEUED', 'QUEUED',
                0, {maxAttempts}, {dbNow}, {dbNow}, {dbNow}, CAST({malwarePayload} AS jsonb)
            )
            ON CONFLICT ("AssetVersionId", "Type", "DefinitionVersion")
            WHERE "Type" NOT IN ('EMBEDDING_GENERATION', 'CODE_ANALYSIS') AND "SecurityRecheckId" IS NULL AND "AnalysisRunId" IS NULL
            DO NOTHING
            """, cancellationToken);

        // 9. Mark ARCHIVE_INSPECTION job as SUCCEEDED
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'SUCCEEDED',
                "Stage" = 'SUCCEEDED',
                "CompletedAt" = {dbNow},
                "UpdatedAt" = {dbNow},
                "Result" = CAST({serializedResult} AS jsonb),
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL
            WHERE "Id" = {jobId}
            """, cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TransitionArchiveInspectionRejected(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default)
    {
        return await TransitionInspectionTerminalFailure(
            jobId, leaseToken, assetId, assetVersionId,
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            AssetVersionProcessingStatus.REJECTED,
            "REJECTED",
            errorCode, safeSummary,
            cancellationToken);
    }

    public async Task<bool> TransitionArchiveInspectionFailed(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default)
    {
        return await TransitionInspectionTerminalFailure(
            jobId, leaseToken, assetId, assetVersionId,
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            AssetVersionProcessingStatus.PROCESSING_FAILED,
            "FAILED",
            errorCode, safeSummary,
            cancellationToken);
    }

    public async Task<bool> TransitionMalwareScanClean(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        MalwareScanResult result,
        MalwareScanEvidence? observedEvidence = null,
        int? observedDefinitionVersion = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(result);

        if (!result.IsClean)
        {
            throw new ArgumentException(
                "TransitionMalwareScanClean requires a clean scan result. Use TransitionMalwareScanRejected for dirty results.",
                nameof(result));
        }

        var serializedResult = AssetProcessingSerializer.SerializeResult(AssetProcessingJobType.MALWARE_SCAN, result);

        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Lock the asset row first. Taking the job lock first and the asset lock afterwards is
        //    the reverse of every other security path and deadlocks against Asset-first enqueue,
        //    finalization and deletion.
        await LockAssetForSecurityTransition(assetId, cancellationToken);

        // 2. Lock job row and retrieve state
        List<JobValidationRecord> jobs = await dbContext.Database.SqlQueryRaw<JobValidationRecord>(
            """
            SELECT "Id", "AssetId", "AssetVersionId", "Type", "Status", "LeaseToken", "LeaseExpiresAt"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
            FOR UPDATE
            """, jobId).ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return false;
        }

        JobValidationRecord job = jobs[0];

        // 3. Capture DB clock timestamp once after lock acquisition
        DateTimeOffset dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

        // 4. Revalidate fencing & job target
        if (job is { Status: "SUCCEEDED", Type: nameof(AssetProcessingJobType.MALWARE_SCAN) }
            && job.AssetId == assetId
            && job.AssetVersionId == assetVersionId)
        {
            await tx.CommitAsync(cancellationToken);
            return true;
        }

        if (job.Status != "RUNNING"
            || job.LeaseToken != leaseToken
            || job.LeaseExpiresAt <= dbNow
            || job.Type != nameof(AssetProcessingJobType.MALWARE_SCAN)
            || job.AssetId != assetId
            || job.AssetVersionId != assetVersionId)
        {
            return false;
        }

        // 5. Re-read the persisted version state; the tracked copy is not authoritative here.
        VersionStatusRecord? freshVersion = await ReadVersionStatusFreshUnderLock(assetId, assetVersionId, cancellationToken);
        if (freshVersion is null)
        {
            return false;
        }

        if (observedEvidence is null && RequiresObservedProof())
        {
            await tx.RollbackAsync(cancellationToken);
            return false;
        }

        // 5b. Atomic proof and scheduling: the observed malware proof, the READY promotion and
        // the durable analysis run/job below commit together. A fault anywhere rolls everything
        // back, so READY is never stranded without its proof and its scheduling. An insufficient
        // observed identity throws for a bounded operational retry. When the pinned policy or the
        // archive proof is absent, the security proof and READY still commit without fabricating
        // analysis.
        if (observedEvidence is not null
            && observedDefinitionVersion is { } malwareDefinitionVersion
            && analysisRunEnqueuer is not null)
        {
            InitialUploadProofResult proof = await analysisRunEnqueuer.EnqueueForInitialUpload(
                assetId,
                assetVersionId,
                freshVersion.ContentSha256,
                malwareDefinitionVersion,
                observedEvidence,
                jobId,
                cancellationToken);

            if (!proof.EvidencePersisted)
            {
                await tx.RollbackAsync(cancellationToken);
                return false;
            }
        }

        // 6. Update candidate version to READY — only from PENDING_MALWARE_SCAN.
        // Publication guards: do not change IsCurrent or enqueue public metadata embeddings here.
        var versionRowsUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_versions
            SET "ProcessingStatus" = 'READY',
                "ProcessingErrorCode" = NULL,
                "ProcessingErrorSummary" = NULL,
                "ProcessingUpdatedAt" = {dbNow}
            WHERE "Id" = {assetVersionId}
              AND "AssetId" = {assetId}
              AND "ProcessingStatus" = 'PENDING_MALWARE_SCAN'
            """, cancellationToken);

        if (versionRowsUpdated == 0)
        {
            // Version is not in the expected PENDING_MALWARE_SCAN state
            await tx.RollbackAsync(cancellationToken);
            return false;
        }

        // 7. Mark MALWARE_SCAN job as SUCCEEDED
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'SUCCEEDED',
                "Stage" = 'READY',
                "CompletedAt" = {dbNow},
                "UpdatedAt" = {dbNow},
                "Result" = CAST({serializedResult} AS jsonb),
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL
            WHERE "Id" = {jobId}
            """, cancellationToken);

        await EnqueueTerminalProcessingNotification(
            assetId,
            assetVersionId,
            AssetVersionProcessingStatus.READY,
            dbNow,
            cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TransitionMalwareScanRejected(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default)
    {
        return await TransitionInspectionTerminalFailure(
            jobId, leaseToken, assetId, assetVersionId,
            AssetProcessingJobType.MALWARE_SCAN,
            AssetVersionProcessingStatus.REJECTED,
            "REJECTED",
            errorCode, safeSummary,
            cancellationToken);
    }

    public async Task<bool> TransitionMalwareScanFailed(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default)
    {
        return await TransitionInspectionTerminalFailure(
            jobId, leaseToken, assetId, assetVersionId,
            AssetProcessingJobType.MALWARE_SCAN,
            AssetVersionProcessingStatus.PROCESSING_FAILED,
            "FAILED",
            errorCode, safeSummary,
            cancellationToken);
    }

    public Task<bool> TransitionProcessingFailed(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        AssetProcessingJobType jobType,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default)
    {
        return jobType switch
        {
            AssetProcessingJobType.ARCHIVE_INSPECTION => TransitionArchiveInspectionFailed(
                jobId, leaseToken, assetId, assetVersionId, errorCode, safeSummary, cancellationToken),
            AssetProcessingJobType.MALWARE_SCAN => TransitionMalwareScanFailed(
                jobId, leaseToken, assetId, assetVersionId, errorCode, safeSummary, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(jobType), jobType, "Only ARCHIVE_INSPECTION and MALWARE_SCAN support processing-failure transitions.")
        };
    }

    private async Task<bool> TransitionInspectionTerminalFailure(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        AssetProcessingJobType expectedType,
        AssetVersionProcessingStatus targetVersionStatus,
        string targetStage,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(errorCode))
        {
            throw new ArgumentException("Error code must not be null or whitespace.", nameof(errorCode));
        }

        if (!_errorCodeRegex.IsMatch(errorCode))
        {
            throw new ArgumentException($"Error code '{errorCode}' does not match required format ^[A-Z0-9_]{{1,64}}$.", nameof(errorCode));
        }

        if (string.IsNullOrWhiteSpace(safeSummary))
        {
            throw new ArgumentException("Safe summary must not be null or whitespace.", nameof(safeSummary));
        }

        var boundedSummary = AssetProcessingJobStore.BoundErrorSummary(safeSummary);
        if (string.IsNullOrWhiteSpace(boundedSummary))
        {
            throw new ArgumentException("Safe summary must contain non-whitespace content.", nameof(safeSummary));
        }

        var expectedTypeStr = expectedType.ToString();
        var targetStatusStr = targetVersionStatus.ToString();
        var expectedSourceStatusStr = expectedType == AssetProcessingJobType.ARCHIVE_INSPECTION
            ? "PENDING_INSPECTION"
            : "PENDING_MALWARE_SCAN";

        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // 1. Lock the asset row first, matching every other security transition.
        await LockAssetForSecurityTransition(assetId, cancellationToken);

        // 2. Lock job row and retrieve state
        List<JobValidationRecord> jobs = await dbContext.Database.SqlQueryRaw<JobValidationRecord>(
            """
            SELECT "Id", "AssetId", "AssetVersionId", "Type", "Status", "LeaseToken", "LeaseExpiresAt"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
            FOR UPDATE
            """, jobId).ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return false;
        }

        JobValidationRecord job = jobs[0];

        // 3. Capture DB clock timestamp once after lock acquisition
        DateTimeOffset dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

        // 4. Revalidate fencing & job target
        if (job.Status == "FAILED"
            && job.Type == expectedTypeStr
            && job.AssetId == assetId
            && job.AssetVersionId == assetVersionId)
        {
            var versionMatches = await dbContext.Database.SqlQueryRaw<int>(
                """
                SELECT 1 AS "Value"
                FROM asset_versions
                WHERE "Id" = {0}
                  AND "AssetId" = {1}
                  AND "ProcessingStatus" = {2}
                  AND "ProcessingErrorCode" = {3}
                """, assetVersionId, assetId, targetStatusStr, errorCode).AnyAsync(cancellationToken);

            if (versionMatches)
            {
                await tx.CommitAsync(cancellationToken);
                return true;
            }

            return false;
        }

        if (job.Status != "RUNNING"
            || job.LeaseToken != leaseToken
            || job.LeaseExpiresAt <= dbNow
            || job.Type != expectedTypeStr
            || job.AssetId != assetId
            || job.AssetVersionId != assetVersionId)
        {
            return false;
        }

        // 5. Re-read the persisted version state under the asset lock.
        VersionStatusRecord? freshVersion = await ReadVersionStatusFreshUnderLock(assetId, assetVersionId, cancellationToken);
        if (freshVersion is null)
        {
            return false;
        }

        // 6. Update asset version from expected source state to rejected or failed
        var versionRowsUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_versions
            SET "ProcessingStatus" = {targetStatusStr},
                "ProcessingErrorCode" = {errorCode},
                "ProcessingErrorSummary" = {boundedSummary},
                "ProcessingUpdatedAt" = {dbNow}
            WHERE "Id" = {assetVersionId}
              AND "AssetId" = {assetId}
              AND "ProcessingStatus" = {expectedSourceStatusStr}
            """, cancellationToken);

        if (versionRowsUpdated != 1)
        {
            return false;
        }

        // 5. Mark job as FAILED
        var jobRowsUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'FAILED',
                "Stage" = {targetStage},
                "CompletedAt" = {dbNow},
                "UpdatedAt" = {dbNow},
                "ErrorCode" = {errorCode},
                "ErrorSummary" = {boundedSummary},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL
            WHERE "Id" = {jobId}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {leaseToken}
            """, cancellationToken);

        if (jobRowsUpdated != 1)
        {
            return false;
        }

        await EnqueueTerminalProcessingNotification(
            assetId,
            assetVersionId,
            targetVersionStatus,
            dbNow,
            cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    public async Task<int> RecoverExpiredExhaustedSecurityJobs(CancellationToken cancellationToken = default)
    {
        var recovered = 0;
        while (recovered < 100)
        {
            if (!await RecoverOneExpiredExhaustedSecurityJob(cancellationToken))
            {
                break;
            }

            recovered++;
        }

        return recovered;
    }

    private async Task<bool> RecoverOneExpiredExhaustedSecurityJob(CancellationToken cancellationToken)
    {
        const string errorCode = ErrorCodes.LEASE_EXPIRED;
        var boundedSummary = AssetProcessingJobStore.BoundErrorSummary(
            ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_EXPIRED));

        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        // Recovery follows the same Asset -> job order as every other security path. The candidate
        // is discovered without a lock, then revalidated after both locks are held.
        List<ExpiredSecurityJobCandidate> candidates = await dbContext.Database.SqlQueryRaw<ExpiredSecurityJobCandidate>(
            """
            SELECT "Id", "AssetId", "AssetVersionId", "Type", "SecurityRecheckId"
            FROM asset_processing_jobs
            WHERE "Status" = 'RUNNING'
              AND "LeaseExpiresAt" IS NOT NULL
              AND "LeaseExpiresAt" <= clock_timestamp()
              AND "AttemptCount" >= "MaxAttempts"
              AND "Type" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN')
            ORDER BY "LeaseExpiresAt", "Id"
            LIMIT 1
            """).ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return false;
        }

        ExpiredSecurityJobCandidate candidate = candidates[0];

        await LockAssetForSecurityTransition(candidate.AssetId, cancellationToken);

        List<JobValidationRecord> lockedJobs = await dbContext.Database.SqlQueryRaw<JobValidationRecord>(
            """
            SELECT "Id", "AssetId", "AssetVersionId", "Type", "Status", "LeaseToken", "LeaseExpiresAt"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
            FOR UPDATE
            """, candidate.Id).ToListAsync(cancellationToken);

        if (lockedJobs.Count == 0)
        {
            return false;
        }

        DateTimeOffset dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

        // Re-read persisted state after the locks; the candidate row may have moved on.
        var recovered = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'FAILED',
                "Stage" = 'FAILED_LEASE_EXPIRED',
                "CompletedAt" = {dbNow},
                "UpdatedAt" = {dbNow},
                "ErrorCode" = {errorCode},
                "ErrorSummary" = {boundedSummary},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL
            WHERE "Id" = {candidate.Id}
              AND "Status" = 'RUNNING'
              AND "LeaseExpiresAt" IS NOT NULL
              AND "LeaseExpiresAt" <= {dbNow}
              AND "AttemptCount" >= "MaxAttempts"
            """, cancellationToken);

        if (recovered != 1)
        {
            return false;
        }

        var expectedSourceStatus = candidate.Type == nameof(AssetProcessingJobType.ARCHIVE_INSPECTION)
            ? nameof(AssetVersionProcessingStatus.PENDING_INSPECTION)
            : nameof(AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        const string failedStatus = nameof(AssetVersionProcessingStatus.PROCESSING_FAILED);

        // Recheck-scoped jobs finalize their own recheck state and never rewrite the version.
        if (candidate.SecurityRecheckId is { } recheckId)
        {
            var recheckRowsUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE security_rechecks
                SET "State" = 'ERROR',
                    "ErrorCode" = {errorCode},
                    "ErrorSummary" = {boundedSummary},
                    "CompletedAt" = {dbNow},
                    "UpdatedAt" = {dbNow}
                WHERE "Id" = {recheckId}
                  AND "State" NOT IN ('SUCCEEDED', 'UNSAFE', 'INFECTED', 'ERROR', 'CANCELLED')
                """, cancellationToken);

            if (recheckRowsUpdated == 1)
            {
                await tx.CommitAsync(cancellationToken);
            }

            return recheckRowsUpdated == 1;
        }

        var versionRowsUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_versions
            SET "ProcessingStatus" = {failedStatus},
                "ProcessingErrorCode" = {errorCode},
                "ProcessingErrorSummary" = {boundedSummary},
                "ProcessingUpdatedAt" = {dbNow}
            WHERE "Id" = {candidate.AssetVersionId}
              AND "AssetId" = {candidate.AssetId}
              AND "ProcessingStatus" = {expectedSourceStatus}
            """, cancellationToken);

        if (versionRowsUpdated == 1)
        {
            await EnqueueTerminalProcessingNotification(
                candidate.AssetId,
                candidate.AssetVersionId,
                AssetVersionProcessingStatus.PROCESSING_FAILED,
                dbNow,
                cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return true;
    }

    /// <summary>
    /// Defense-in-depth bar for observed archive proof: the exact advertised hash over the exact
    /// advertised byte scope, fully consumed, reported by the actual inspector implementation.
    /// </summary>
    private static bool IsObservedArchiveSufficient(ArchiveValidationEvidence evidence, VersionStatusRecord version) =>
        evidence.ScopeComplete
        && evidence.ScannedByteCount == version.ContentLength
        && string.Equals(evidence.ContentSha256, version.ContentSha256, StringComparison.Ordinal)
        && !string.IsNullOrWhiteSpace(evidence.ImplementationIdentity);

    private async Task InsertInitialArchiveEvidence(
        Guid assetId,
        Guid assetVersionId,
        Guid jobId,
        ArchiveValidationEvidence evidence,
        int definitionVersion,
        DateTimeOffset dbNow,
        CancellationToken cancellationToken)
    {
        var digest = ProductDigests.BuildArchiveEvidenceDigest(
            assetId, assetVersionId, evidence, 1, definitionVersion,
            AssetProcessingDefaults.MALWARE_POLICY_VERSION, dbNow);

        var id = Guid.NewGuid();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO security_evidence_records (
                "Id", "AssetId", "AssetVersionId", "ContentSha256", "SecurityRecheckId", "EvidenceKind",
                "JobId", "Generation", "DefinitionVersion", "Outcome", "SecurityPolicyVersion",
                "ImplementationIdentity", "IdentityAvailable", "ScannedByteCount", "ScannedEntryCount",
                "ScopeComplete", "CompletedAt", "EvidenceDigest", "CreatedAt"
            ) VALUES (
                {id}, {assetId}, {assetVersionId}, {evidence.ContentSha256},
                NULL, 'ARCHIVE_VALIDATION', {jobId}, 1,
                {definitionVersion}, 'VALID', {AssetProcessingDefaults.MALWARE_POLICY_VERSION},
                {evidence.ImplementationIdentity}, false, {evidence.ScannedByteCount}, {evidence.FileCount},
                {evidence.ScopeComplete}, {dbNow}, {digest}, {dbNow}
            )
            ON CONFLICT ("AssetVersionId", "EvidenceKind") WHERE "SecurityRecheckId" IS NULL
            DO NOTHING
            """, cancellationToken).ConfigureAwait(false);
    }

    private async Task EnqueueTerminalProcessingNotification(
        Guid assetId,
        Guid assetVersionId,
        AssetVersionProcessingStatus status,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        (NotificationKind kind, var hubMethod) = status switch
        {
            AssetVersionProcessingStatus.READY => (
                NotificationKind.ASSET_PROCESSING_READY,
                NotificationHubMethods.ASSET_PROCESSING_READY),
            AssetVersionProcessingStatus.REJECTED => (
                NotificationKind.ASSET_PROCESSING_REJECTED,
                NotificationHubMethods.ASSET_PROCESSING_REJECTED),
            AssetVersionProcessingStatus.PROCESSING_FAILED => (
                NotificationKind.ASSET_PROCESSING_FAILED,
                NotificationHubMethods.ASSET_PROCESSING_FAILED),
            _ => throw new ArgumentOutOfRangeException(
                nameof(status),
                status,
                "Only terminal security statuses enqueue notifications.")
        };

        List<AssetOwnerRow> owners = await dbContext.Database.SqlQueryRaw<AssetOwnerRow>(
            """
            SELECT "AuthorId", "Title"
            FROM assets
            WHERE "Id" = {0}
            """, assetId).ToListAsync(cancellationToken);

        if (owners.Count == 0)
        {
            return;
        }

        AssetOwnerRow owner = owners[0];
        var notificationId = Guid.NewGuid();
        var outboxId = Guid.NewGuid();
        var metadata = JsonSerializer.Serialize(
            new AssetProcessingTerminalMessage(
                notificationId,
                assetId,
                assetVersionId,
                status.ToString(),
                owner.Title),
            _jsonOptions);

        if (metadata.Length > NotificationConstraints.MAX_METADATA_JSON_LENGTH)
        {
            return;
        }

        var payload = JsonSerializer.Serialize(
            new NotificationDispatchPayload(owner.AuthorId, kind, hubMethod, metadata),
            _jsonOptions);

        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO outbox_messages ("Id", "Type", "Payload", "OccurredAt", "Status", "AttemptCount", "ReplayCount")
            VALUES ({outboxId}, {OutboxMessageTypes.NOTIFICATION_DISPATCH}, {payload}, {occurredAt}, {(int)OutboxMessageStatus.PENDING}, 0, 0)
            """, cancellationToken);

        var kindName = kind.ToString();
        await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO user_notifications ("Id", "RecipientUserId", "Kind", "MetadataJson", "CreatedAt", "SourceOutboxMessageId")
            VALUES ({notificationId}, {owner.AuthorId}, {kindName}, {metadata}, {occurredAt}, {outboxId})
            """, cancellationToken);
    }

    [GeneratedRegex("^[A-Z0-9_]{1,64}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}
