using System.Data;
using System.Diagnostics;
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

internal sealed partial class AssetProcessingJobStore(ApplicationDbContext dbContext, ILogger<AssetProcessingJobStore> logger, IOptions<AssetProcessingOptions> options) : IAssetProcessingJobStore
{
    private readonly AssetProcessingOptions _options = options.Value;
    private const int MAX_ERROR_SUMMARY_RUNES = 2000;

    [GeneratedRegex("^[A-Z0-9_]{1,64}$")]
    private static partial Regex ErrorCodeRegex();

    internal static string BoundErrorSummary(string errorSummary)
    {
        ArgumentNullException.ThrowIfNull(errorSummary);

        var runeCount = 0;
        var utf16CharCount = 0;

        foreach (var rune in errorSummary.EnumerateRunes())
        {
            if (runeCount >= MAX_ERROR_SUMMARY_RUNES)
            {
                break;
            }

            runeCount++;
            utf16CharCount += rune.Utf16SequenceLength;
        }

        return errorSummary[..utf16CharCount];
    }

    public async Task<Guid> Enqueue(
        Guid assetId,
        Guid assetVersionId,
        AssetProcessingJobType type,
        int definitionVersion,
        TimeSpan initialDelay,
        AssetProcessingPayload payload,
        string? traceParent = null,
        CancellationToken cancellationToken = default)
    {
        if (initialDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(initialDelay), "Initial delay must be non-negative.");
        }

        if (traceParent != null && !ActivityContext.TryParse(traceParent, null, out _))
        {
            throw new ArgumentException("TraceParent must be a valid W3C traceparent.", nameof(traceParent));
        }

        var serializedPayload = AssetProcessingSerializer.SerializePayload(type, payload);
        var newId = Guid.NewGuid();
        var typeString = type.ToString();
        var maxAttempts = _options.MaxAttempts;

        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var cmd = connection.CreateCommand();
        cmd.Transaction = dbContext.Database.CurrentTransaction?.GetDbTransaction();
        cmd.CommandText = """
            WITH inserted AS (
                INSERT INTO asset_processing_jobs (
                    "Id",
                    "AssetId",
                    "AssetVersionId",
                    "Type",
                    "DefinitionVersion",
                    "Status",
                    "Stage",
                    "AttemptCount",
                    "MaxAttempts",
                    "AvailableAt",
                    "Payload",
                    "TraceParent",
                    "CreatedAt"
                )
                VALUES (
                    @id,
                    @assetId,
                    @assetVersionId,
                    @type,
                    @defVer,
                    'QUEUED',
                    'QUEUED',
                    0,
                    @maxAttempts,
                    clock_timestamp() + @initialDelay,
                    CAST(@payload AS jsonb),
                    @traceParent,
                    clock_timestamp()
                )
                ON CONFLICT ("AssetVersionId", "Type", "DefinitionVersion") DO NOTHING
                RETURNING "Id"
            )
            SELECT "Id" FROM inserted
            UNION ALL
            SELECT j."Id"
            FROM asset_processing_jobs j
            WHERE j."AssetVersionId" = @assetVersionId
              AND j."Type" = @type
              AND j."DefinitionVersion" = @defVer
            LIMIT 1;
            """;

        var pId = cmd.CreateParameter();
        pId.ParameterName = "@id";
        pId.Value = newId;
        cmd.Parameters.Add(pId);

        var pAssetId = cmd.CreateParameter();
        pAssetId.ParameterName = "@assetId";
        pAssetId.Value = assetId;
        cmd.Parameters.Add(pAssetId);

        var pVersionId = cmd.CreateParameter();
        pVersionId.ParameterName = "@assetVersionId";
        pVersionId.Value = assetVersionId;
        cmd.Parameters.Add(pVersionId);

        var pType = cmd.CreateParameter();
        pType.ParameterName = "@type";
        pType.Value = typeString;
        cmd.Parameters.Add(pType);

        var pDefVer = cmd.CreateParameter();
        pDefVer.ParameterName = "@defVer";
        pDefVer.Value = definitionVersion;
        cmd.Parameters.Add(pDefVer);

        var pMaxAttempts = cmd.CreateParameter();
        pMaxAttempts.ParameterName = "@maxAttempts";
        pMaxAttempts.Value = maxAttempts;
        cmd.Parameters.Add(pMaxAttempts);

        var pDelay = cmd.CreateParameter();
        pDelay.ParameterName = "@initialDelay";
        pDelay.Value = initialDelay;
        cmd.Parameters.Add(pDelay);

        var pPayload = cmd.CreateParameter();
        pPayload.ParameterName = "@payload";
        pPayload.Value = serializedPayload;
        cmd.Parameters.Add(pPayload);

        var pTrace = cmd.CreateParameter();
        pTrace.ParameterName = "@traceParent";
        pTrace.Value = (object?)traceParent ?? DBNull.Value;
        cmd.Parameters.Add(pTrace);

        var result = await cmd.ExecuteScalarAsync(cancellationToken);
        if (result is null or DBNull)
        {
            cmd.CommandText = """
                SELECT j."Id"
                FROM asset_processing_jobs j
                WHERE j."AssetVersionId" = @assetVersionId
                  AND j."Type" = @type
                  AND j."DefinitionVersion" = @defVer
                LIMIT 1;
                """;
            result = await cmd.ExecuteScalarAsync(cancellationToken);
        }

        var jobId = (Guid)result!;

        logger.LogDebug("Enqueued processing job {JobId} of type {Type}", jobId, type);
        return jobId;
    }

    public async Task<IReadOnlyList<ClaimedAssetProcessingJob>> ClaimPendingBatch(
        int batchSize,
        TimeSpan leaseDuration,
        string leaseOwner,
        CancellationToken cancellationToken = default)
    {
        if (batchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        var leaseToken = Guid.NewGuid();

        await using var tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var claimedCount = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            WITH claimable AS (
                SELECT j."Id"
                FROM asset_processing_jobs AS j
                WHERE j."Status" IN ('QUEUED', 'RETRY_SCHEDULED')
                  AND j."AvailableAt" <= clock_timestamp()
                  AND j."AttemptCount" < j."MaxAttempts"
                ORDER BY j."AvailableAt", j."Id"
                FOR UPDATE SKIP LOCKED
                LIMIT {batchSize}
            )
            UPDATE asset_processing_jobs AS j
            SET "Status" = 'RUNNING',
                "Stage" = 'RUNNING',
                "LeaseOwner" = {leaseOwner},
                "LeaseToken" = {leaseToken},
                "LeaseExpiresAt" = clock_timestamp() + {leaseDuration},
                "StartedAt" = COALESCE(j."StartedAt", clock_timestamp()),
                "AttemptCount" = j."AttemptCount" + 1,
                "UpdatedAt" = clock_timestamp()
            FROM claimable c
            WHERE j."Id" = c."Id"
            """, cancellationToken);

        if (claimedCount == 0)
        {
            await tx.CommitAsync(cancellationToken);
            return [];
        }

        var claimed = await dbContext.AssetProcessingJobs
            .AsNoTracking()
            .Where(j => j.LeaseToken == leaseToken)
            .OrderBy(j => j.AvailableAt)
            .ThenBy(j => j.Id)
            .Select(j => new ClaimedAssetProcessingJob(
                j.Id,
                j.AssetId,
                j.AssetVersionId,
                j.Asset.AuthorId,
                j.Type,
                j.DefinitionVersion,
                j.AttemptCount,
                j.MaxAttempts,
                j.Payload!,
                j.TraceParent,
                j.LeaseToken!.Value,
                j.AvailableAt,
                j.CreatedAt,
                j.UpdatedAt
            ))
            .ToListAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task<bool> RenewLease(
        Guid jobId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var locked = await dbContext.Database.SqlQueryRaw<Guid>(
            """
            SELECT "Id"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {1}
            FOR UPDATE
            """, jobId, leaseToken).ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            return false;
        }

        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "LeaseExpiresAt" = clock_timestamp() + {leaseDuration},
                "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {jobId}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {leaseToken}
              AND "LeaseExpiresAt" > clock_timestamp()
            """, cancellationToken);

        if (updated > 0)
        {
            await tx.CommitAsync(cancellationToken);
            return true;
        }

        return false;
    }

    public async Task<bool> MarkSucceeded(
        Guid jobId,
        Guid leaseToken,
        AssetProcessingResult? result,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var jobTypes = await dbContext.Database.SqlQueryRaw<string>(
            """
            SELECT "Type"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {1}
            FOR UPDATE
            """, jobId, leaseToken).ToListAsync(cancellationToken);

        if (jobTypes.Count == 0)
        {
            return false;
        }

        var jobType = Enum.Parse<AssetProcessingJobType>(jobTypes[0]);
        var serializedResult = result != null ? AssetProcessingSerializer.SerializeResult(jobType, result) : null;

        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'SUCCEEDED',
                "Stage" = 'SUCCEEDED',
                "CompletedAt" = clock_timestamp(),
                "Result" = {serializedResult}::jsonb,
                "ErrorCode" = NULL,
                "ErrorSummary" = NULL,
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {jobId}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {leaseToken}
              AND "LeaseExpiresAt" > clock_timestamp()
            """, cancellationToken);

        if (updated > 0)
        {
            await tx.CommitAsync(cancellationToken);
            return true;
        }

        return false;
    }

    public async Task<bool> MarkFailedRetryable(
        Guid jobId,
        Guid leaseToken,
        string errorCode,
        string errorSummary,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || !ErrorCodeRegex().IsMatch(errorCode))
        {
            throw new ArgumentException("ErrorCode must be 1-64 uppercase alphanumeric characters or underscores.", nameof(errorCode));
        }

        if (retryDelay < TimeSpan.Zero || retryDelay > _options.MaxRetryDelay)
        {
            throw new ArgumentOutOfRangeException(nameof(retryDelay), $"Retry delay must be between 0 and {_options.MaxRetryDelay}.");
        }

        var boundedSummary = BoundErrorSummary(errorSummary);

        await using var tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var locked = await dbContext.Database.SqlQueryRaw<Guid>(
            """
            SELECT "Id"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {1}
            FOR UPDATE
            """, jobId, leaseToken).ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            return false;
        }

        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = CASE WHEN "AttemptCount" < "MaxAttempts" THEN 'RETRY_SCHEDULED' ELSE 'FAILED' END,
                "Stage" = CASE WHEN "AttemptCount" < "MaxAttempts" THEN 'RETRY_SCHEDULED' ELSE 'FAILED_ATTEMPTS_EXHAUSTED' END,
                "CompletedAt" = CASE WHEN "AttemptCount" < "MaxAttempts" THEN "CompletedAt" ELSE clock_timestamp() END,
                "AvailableAt" = CASE WHEN "AttemptCount" < "MaxAttempts" THEN clock_timestamp() + {retryDelay} ELSE "AvailableAt" END,
                "ErrorCode" = {errorCode},
                "ErrorSummary" = {boundedSummary},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {jobId}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {leaseToken}
              AND "LeaseExpiresAt" > clock_timestamp()
            """, cancellationToken);

        if (updated > 0)
        {
            await tx.CommitAsync(cancellationToken);
            return true;
        }

        return false;
    }

    public async Task<bool> MarkFailedTerminal(
        Guid jobId,
        Guid leaseToken,
        string errorCode,
        string errorSummary,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(errorCode) || !ErrorCodeRegex().IsMatch(errorCode))
        {
            throw new ArgumentException("ErrorCode must be 1-64 uppercase alphanumeric characters or underscores.", nameof(errorCode));
        }

        var boundedSummary = BoundErrorSummary(errorSummary);

        await using var tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var locked = await dbContext.Database.SqlQueryRaw<Guid>(
            """
            SELECT "Id"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {1}
            FOR UPDATE
            """, jobId, leaseToken).ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            return false;
        }

        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'FAILED',
                "Stage" = 'FAILED',
                "CompletedAt" = clock_timestamp(),
                "ErrorCode" = {errorCode},
                "ErrorSummary" = {boundedSummary},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {jobId}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {leaseToken}
              AND "LeaseExpiresAt" > clock_timestamp()
            """, cancellationToken);

        if (updated > 0)
        {
            await tx.CommitAsync(cancellationToken);
            return true;
        }

        return false;
    }

    public async Task<bool> MarkCancelled(
        Guid jobId,
        Guid leaseToken,
        CancellationToken cancellationToken = default)
    {
        await using var tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var locked = await dbContext.Database.SqlQueryRaw<Guid>(
            """
            SELECT "Id"
            FROM asset_processing_jobs
            WHERE "Id" = {0}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {1}
            FOR UPDATE
            """, jobId, leaseToken).ToListAsync(cancellationToken);

        if (locked.Count == 0)
        {
            return false;
        }

        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE asset_processing_jobs
            SET "Status" = 'CANCELLED',
                "Stage" = 'CANCELLED',
                "CompletedAt" = clock_timestamp(),
                "ErrorCode" = NULL,
                "ErrorSummary" = NULL,
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "UpdatedAt" = clock_timestamp()
            WHERE "Id" = {jobId}
              AND "Status" = 'RUNNING'
              AND "LeaseToken" = {leaseToken}
              AND "LeaseExpiresAt" > clock_timestamp()
            """, cancellationToken);

        if (updated > 0)
        {
            await tx.CommitAsync(cancellationToken);
            return true;
        }

        return false;
    }

    public async Task<int> RecoverExpiredLeases(CancellationToken cancellationToken = default)
    {
        var leaseExpiredCode = ErrorCodes.LEASE_EXPIRED;
        var leaseExpiredSummary = ErrorCodesToErrorMessages.GetMessage(ErrorCodes.LEASE_EXPIRED);
        var updated = await dbContext.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS (
                SELECT j."Id"
                FROM asset_processing_jobs AS j
                WHERE j."Status" = 'RUNNING'
                  AND j."LeaseExpiresAt" IS NOT NULL
                  AND j."LeaseExpiresAt" <= clock_timestamp()
                  AND NOT (
                      j."AttemptCount" >= j."MaxAttempts"
                      AND j."Type" IN ('ARCHIVE_INSPECTION', 'MALWARE_SCAN')
                  )
                ORDER BY j."LeaseExpiresAt", j."Id"
                FOR UPDATE SKIP LOCKED
                LIMIT 100
            )
            UPDATE asset_processing_jobs AS j
            SET "Status" = CASE WHEN j."AttemptCount" < j."MaxAttempts" THEN 'RETRY_SCHEDULED' ELSE 'FAILED' END,
                "Stage" = CASE WHEN j."AttemptCount" < j."MaxAttempts" THEN 'LEASE_RECOVERED' ELSE 'FAILED_LEASE_EXPIRED' END,
                "CompletedAt" = CASE WHEN j."AttemptCount" < j."MaxAttempts" THEN j."CompletedAt" ELSE clock_timestamp() END,
                "AvailableAt" = CASE WHEN j."AttemptCount" < j."MaxAttempts" THEN clock_timestamp() ELSE j."AvailableAt" END,
                "ErrorCode" = {leaseExpiredCode},
                "ErrorSummary" = {leaseExpiredSummary},
                "LeaseOwner" = NULL,
                "LeaseToken" = NULL,
                "LeaseExpiresAt" = NULL,
                "UpdatedAt" = clock_timestamp()
            FROM expired e
            WHERE j."Id" = e."Id"
            """, cancellationToken);

        if (updated > 0)
        {
            logger.LogWarning("Recovered {Count} expired asset processing jobs.", updated);
        }

        return updated;
    }

    public async Task<IReadOnlyList<AssetProcessingJobDto>?> GetJobsForAsset(
        Guid assetId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var assetExists = await dbContext.Assets
            .AsNoTracking()
            .AnyAsync(a => a.Id == assetId && a.AuthorId == ownerUserId, cancellationToken);

        if (!assetExists)
        {
            return null;
        }

        return await dbContext.AssetProcessingJobs
            .AsNoTracking()
            .Where(j => j.AssetId == assetId && j.Asset.AuthorId == ownerUserId)
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Select(j => new AssetProcessingJobDto(
                j.Id,
                j.AssetId,
                j.AssetVersionId,
                j.Type,
                j.DefinitionVersion,
                j.Status,
                j.Stage,
                j.AttemptCount,
                j.MaxAttempts,
                j.AvailableAt,
                j.StartedAt,
                j.CompletedAt,
                j.ErrorCode,
                j.ErrorSummary,
                j.CreatedAt,
                j.UpdatedAt
            ))
            .Take(50)
            .ToListAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<AssetProcessingJobDto>?> GetJobsForVersion(
        Guid assetVersionId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default)
    {
        var versionExists = await dbContext.AssetVersions
            .AsNoTracking()
            .AnyAsync(v => v.Id == assetVersionId && v.Asset.AuthorId == ownerUserId, cancellationToken);

        if (!versionExists)
        {
            return null;
        }

        return await dbContext.AssetProcessingJobs
            .AsNoTracking()
            .Where(j => j.AssetVersionId == assetVersionId && j.Asset.AuthorId == ownerUserId)
            .OrderByDescending(j => j.CreatedAt)
            .ThenByDescending(j => j.Id)
            .Select(j => new AssetProcessingJobDto(
                j.Id,
                j.AssetId,
                j.AssetVersionId,
                j.Type,
                j.DefinitionVersion,
                j.Status,
                j.Stage,
                j.AttemptCount,
                j.MaxAttempts,
                j.AvailableAt,
                j.StartedAt,
                j.CompletedAt,
                j.ErrorCode,
                j.ErrorSummary,
                j.CreatedAt,
                j.UpdatedAt
            ))
            .Take(50)
            .ToListAsync(cancellationToken);
    }

    public async Task<AssetProcessingJobRealtimeState?> GetRealtimeState(
        Guid jobId,
        CancellationToken cancellationToken = default)
    {
        return await dbContext.AssetProcessingJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new AssetProcessingJobRealtimeState(
                j.Id,
                j.AssetId,
                j.AssetVersionId,
                j.Asset.AuthorId,
                j.Type,
                j.Status,
                j.Stage,
                j.UpdatedAt
            ))
            .FirstOrDefaultAsync(cancellationToken);
    }
}
