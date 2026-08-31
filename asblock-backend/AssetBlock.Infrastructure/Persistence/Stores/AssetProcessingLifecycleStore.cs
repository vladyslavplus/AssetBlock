using System.Data;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Persistence.Stores;

public sealed partial class AssetProcessingLifecycleStore(
    ApplicationDbContext dbContext,
    IOptions<AssetProcessingOptions> options)
    : IAssetProcessingLifecycleStore
{
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

    public async Task<bool> TransitionArchiveInspectionAccepted(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        ArchiveInspectionResult result,
        BoundedArchiveAnalysisRecord analysis,
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

        // 1. Lock job row and retrieve state
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

        // 2. Capture DB clock timestamp once after lock acquisition
        DateTimeOffset dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

        // 3. Revalidate fencing & job target
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

        // 4. Upsert archive analysis record
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

        // 5. Update asset version processing status — only from PENDING_INSPECTION
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

        // 6. Enqueue exactly one MALWARE_SCAN job idempotently
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
            ON CONFLICT ("AssetVersionId", "Type", "DefinitionVersion") DO NOTHING
            """, cancellationToken);

        // 7. Mark ARCHIVE_INSPECTION job as SUCCEEDED
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

        // 1. Lock job row and retrieve state
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

        // 2. Capture DB clock timestamp once after lock acquisition
        DateTimeOffset dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

        // 3. Revalidate fencing & job target
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

        // 4. Lock asset row for atomic monotonic promotion
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""SELECT "Id" FROM assets WHERE "Id" = {assetId} FOR UPDATE""", cancellationToken);

        // Get candidate version number
        List<int> candidateVersionNumbers = await dbContext.Database.SqlQueryRaw<int>(
            """
            SELECT "VersionNumber" AS "Value"
            FROM asset_versions
            WHERE "Id" = {0} AND "AssetId" = {1}
            """, assetVersionId, assetId).ToListAsync(cancellationToken);

        if (candidateVersionNumbers.Count == 0)
        {
            return false;
        }

        var candidateVersionNumber = candidateVersionNumbers[0];

        // Get current version number if exists
        List<int> currentVersionNumbers = await dbContext.Database.SqlQueryRaw<int>(
            """
            SELECT "VersionNumber" AS "Value"
            FROM asset_versions
            WHERE "AssetId" = {0} AND "IsCurrent" = true
            """, assetId).ToListAsync(cancellationToken);

        var currentVersionNumber = currentVersionNumbers.Count > 0 ? (int?)currentVersionNumbers[0] : null;

        var shouldPromote = currentVersionNumber == null || candidateVersionNumber > currentVersionNumber.Value;

        if (shouldPromote)
        {
            // Demote previous current version before promoting the candidate
            await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE asset_versions
                SET "IsCurrent" = false
                WHERE "AssetId" = {assetId}
                  AND "IsCurrent" = true
                  AND "Id" != {assetVersionId}
                """, cancellationToken);
        }

        // 5. Update candidate version to READY — only from PENDING_MALWARE_SCAN
        var versionRowsUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_versions
            SET "ProcessingStatus" = 'READY',
                "ProcessingErrorCode" = NULL,
                "ProcessingErrorSummary" = NULL,
                "ProcessingUpdatedAt" = {dbNow},
                "IsCurrent" = CASE WHEN {shouldPromote} THEN true ELSE "IsCurrent" END
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

        // 6. Mark MALWARE_SCAN job as SUCCEEDED
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

        // 1. Lock job row and retrieve state
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

        // 2. Capture DB clock timestamp once after lock acquisition
        DateTimeOffset dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

        // 3. Revalidate fencing & job target
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

        // 4. Update asset version from expected source state to rejected or failed
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
        var errorCode = ErrorCodes.LEASE_EXPIRED;
        var boundedSummary = AssetProcessingJobStore.BoundErrorSummary(
            ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_EXPIRED));

        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        List<AssetProcessingJob> jobs = await dbContext.AssetProcessingJobs
            .FromSqlInterpolated($"""
                SELECT *
                FROM asset_processing_jobs AS j
                WHERE j."Status" = 'RUNNING'
                  AND j."LeaseExpiresAt" IS NOT NULL
                  AND j."LeaseExpiresAt" <= clock_timestamp()
                  AND j."AttemptCount" >= j."MaxAttempts"
                  AND j."Type" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN')
                ORDER BY j."LeaseExpiresAt", j."Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 1
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (jobs.Count == 0)
        {
            return false;
        }

        AssetProcessingJob job = jobs[0];
        var expectedSourceStatus = job.Type == AssetProcessingJobType.ARCHIVE_INSPECTION
            ? nameof(AssetVersionProcessingStatus.PENDING_INSPECTION)
            : nameof(AssetVersionProcessingStatus.PENDING_MALWARE_SCAN);
        var failedStatus = nameof(AssetVersionProcessingStatus.PROCESSING_FAILED);

        DateTimeOffset dbNow = await dbContext.Database.SqlQueryRaw<DateTimeOffset>(
            """SELECT clock_timestamp() AS "Value" """).FirstAsync(cancellationToken);

        var versionRowsUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_versions
            SET "ProcessingStatus" = {failedStatus},
                "ProcessingErrorCode" = {errorCode},
                "ProcessingErrorSummary" = {boundedSummary},
                "ProcessingUpdatedAt" = {dbNow}
            WHERE "Id" = {job.AssetVersionId}
              AND "AssetId" = {job.AssetId}
              AND "ProcessingStatus" = {expectedSourceStatus}
            """, cancellationToken);

        var jobRowsUpdated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
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
            WHERE "Id" = {job.Id}
              AND "Status" = 'RUNNING'
            """, cancellationToken);

        if (jobRowsUpdated != 1)
        {
            return false;
        }

        if (versionRowsUpdated == 1)
        {
            await EnqueueTerminalProcessingNotification(
                job.AssetId,
                job.AssetVersionId,
                AssetVersionProcessingStatus.PROCESSING_FAILED,
                dbNow,
                cancellationToken);
        }

        await tx.CommitAsync(cancellationToken);
        return true;
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
