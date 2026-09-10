using System.Collections.Concurrent;
using System.Data;
using System.Diagnostics;
using System.Globalization;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Observability;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Services;

internal sealed class EmbeddingBackfillCoordinator(
    ApplicationDbContext dbContext,
    IAssetProcessingJobStore jobStore,
    ITextEmbeddingGenerator textEmbeddingGenerator,
    IOptions<EmbeddingOptions> embeddingOptions,
    ILogger<EmbeddingBackfillCoordinator> logger,
    TimeProvider? timeProvider = null,
    ICacheService? cacheService = null) : IEmbeddingBackfillCoordinator
{
    private const long ADVISORY_LOCK_KEY = 0x4153424C4F434B03L;
    private const string VERSION_STATUS_READY = nameof(AssetVersionProcessingStatus.READY);
    private const string CURSOR_CACHE_PREFIX = "embedding:backfill:cursor:";
    private static readonly ConcurrentDictionary<string, int> _fallbackCursors = new();

    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    internal static void ResetFallbackCursors() => _fallbackCursors.Clear();

    internal async Task<int> GetCursorOffset(string modelKey, CancellationToken cancellationToken)
    {
        if (cacheService != null)
        {
            try
            {
                var cached = await cacheService.GetString($"{CURSOR_CACHE_PREFIX}{modelKey}", cancellationToken);
                if (int.TryParse(cached, CultureInfo.InvariantCulture, out var parsed) && parsed >= 0)
                {
                    return parsed;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to read backfill cursor from cache; falling back to memory");
            }
        }

        return _fallbackCursors.GetValueOrDefault(modelKey, 0);
    }

    internal async Task SetCursorOffset(string modelKey, int offset, CancellationToken cancellationToken)
    {
        _fallbackCursors[modelKey] = offset;

        if (cacheService != null)
        {
            try
            {
                await cacheService.SetString(
                    $"{CURSOR_CACHE_PREFIX}{modelKey}",
                    offset.ToString(CultureInfo.InvariantCulture),
                    TimeSpan.FromDays(7),
                    cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist backfill cursor to cache");
            }
        }
    }

    public async Task<int> RunBackfillCycle(CancellationToken cancellationToken = default)
    {
        var stopwatch = Stopwatch.StartNew();
        EmbeddingOptions options = embeddingOptions.Value;

        if (!options.Enabled)
        {
            AssetBlockDiagnostics.RecordEmbeddingBackfillCycle(stopwatch.Elapsed, 0, EmbeddingBackfillOutcomes.DISABLED);
            return 0;
        }

        ModelVerificationResult availability = await textEmbeddingGenerator.CheckModelAvailability(cancellationToken);
        if (!availability.IsAvailable)
        {
            logger.LogWarning("Embedding model is unavailable: {Reason}. Skipping backfill cycle.", availability.FailureReason);
            AssetBlockDiagnostics.RecordEmbeddingBackfillCycle(stopwatch.Elapsed, 0, EmbeddingBackfillOutcomes.UNAVAILABLE);
            return 0;
        }

        var modelKey = EmbeddingModelKey.Compute(options);

        // Read cursor BEFORE opening DB transaction so cache delay/timeout never holds a DB transaction or lock
        var startOffset = await GetCursorOffset(modelKey, cancellationToken);

        await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var lockAcquired = await dbContext.Database.SqlQueryRaw<bool>(
            $"SELECT pg_try_advisory_xact_lock({ADVISORY_LOCK_KEY}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        if (!lockAcquired)
        {
            logger.LogDebug("Embedding backfill advisory lock already held; skipping cycle");
            await tx.RollbackAsync(cancellationToken);
            AssetBlockDiagnostics.RecordEmbeddingBackfillCycle(stopwatch.Elapsed, 0, EmbeddingBackfillOutcomes.LOCKED);
            return 0;
        }

        DateTimeOffset cooldownCutoff = _timeProvider.GetUtcNow().AddHours(-1);
        var batchLimit = Math.Clamp(options.BackfillBatchSize, 1, 50);
        var currentOffset = startOffset;
        var candidatesScannedThisCycle = 0;
        var enqueuedCount = 0;
        var hasWrapped = false;
        const int maxCandidatesToScan = 500;

        while (enqueuedCount < batchLimit && candidatesScannedThisCycle < maxCandidatesToScan)
        {
            var limit = Math.Min(batchLimit, maxCandidatesToScan - candidatesScannedThisCycle);
            List<Guid> eligibleAssetIds = await dbContext.Database.SqlQueryRaw<Guid>(
                """
                SELECT a."Id" AS "Value"
                FROM assets a
                JOIN asset_versions v ON v."AssetId" = a."Id" AND v."IsCurrent" = true AND v."ProcessingStatus" = {0}
                WHERE a."DeletedAt" IS NULL
                  AND NOT EXISTS (
                      SELECT 1
                      FROM asset_embeddings ae
                      WHERE ae."AssetId" = a."Id"
                        AND ae."ModelKey" = {1}
                        AND ae."SourceRevision" >= a."SearchRevision"
                  )
                ORDER BY a."SearchRevision" DESC, a."UpdatedAt" DESC, a."Id" DESC
                OFFSET {2}
                LIMIT {3}
                """,
                VERSION_STATUS_READY,
                modelKey,
                currentOffset,
                limit).ToListAsync(cancellationToken);

            if (eligibleAssetIds.Count == 0)
            {
                if (!hasWrapped && currentOffset > 0)
                {
                    hasWrapped = true;
                    currentOffset = 0;
                    continue;
                }

                currentOffset = 0;
                break;
            }

            candidatesScannedThisCycle += eligibleAssetIds.Count;
            currentOffset += eligibleAssetIds.Count;

            foreach (Guid assetId in eligibleAssetIds)
            {
                if (enqueuedCount >= batchLimit)
                {
                    break;
                }

                var assetData = await dbContext.Assets
                    .AsNoTracking()
                    .Where(a => a.Id == assetId && a.DeletedAt == null)
                    .Select(a => new
                    {
                        a.SearchRevision,
                        a.Title,
                        a.Description,
                        CategoryName = a.Category.Name
                    })
                    .FirstOrDefaultAsync(cancellationToken);

                if (assetData == null)
                {
                    continue;
                }

                Guid? readyVersionId = await dbContext.AssetVersions
                    .AsNoTracking()
                    .Where(v => v.AssetId == assetId && v.IsCurrent && v.ProcessingStatus == AssetVersionProcessingStatus.READY)
                    .Select(v => (Guid?)v.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                if (!readyVersionId.HasValue)
                {
                    continue;
                }

                List<string> tagNames = await dbContext.AssetTags
                    .AsNoTracking()
                    .Where(at => at.AssetId == assetId)
                    .OrderBy(at => at.Tag.Name)
                    .Select(at => at.Tag.Name)
                    .ToListAsync(cancellationToken);

                CanonicalPublicMetadataResult canonical = AssetPublicMetadataCanonicalizer.Canonicalize(
                    assetData.Title,
                    assetData.Description,
                    assetData.CategoryName,
                    tagNames);

                // 1. Check if there is already an active job for this exact (AssetId, ModelKey, ContentHash) identity tuple
                var hasActiveJob = await dbContext.AssetProcessingJobs
                    .AsNoTracking()
                    .AnyAsync(q =>
                        q.AssetId == assetId
                        && q.Type == AssetProcessingJobType.EMBEDDING_GENERATION
                        && q.ModelKey == modelKey
                        && q.InputHash == canonical.ContentHash
                        && (q.Status == AssetProcessingJobStatus.QUEUED
                            || q.Status == AssetProcessingJobStatus.RUNNING
                            || q.Status == AssetProcessingJobStatus.RETRY_SCHEDULED),
                        cancellationToken);

                if (hasActiveJob)
                {
                    continue;
                }

                // 2. Cooldown check is strictly bound to the (AssetId, ModelKey, ContentHash) identity tuple
                var isCoolingDown = await dbContext.AssetProcessingJobs
                    .AsNoTracking()
                    .AnyAsync(f =>
                        f.AssetId == assetId
                        && f.Type == AssetProcessingJobType.EMBEDDING_GENERATION
                        && f.ModelKey == modelKey
                        && f.InputHash == canonical.ContentHash
                        && f.Status == AssetProcessingJobStatus.FAILED
                        && (f.CompletedAt >= cooldownCutoff || f.UpdatedAt >= cooldownCutoff),
                        cancellationToken);

                if (isCoolingDown)
                {
                    continue;
                }

                var payload = new EmbeddingGenerationPayload(
                    assetId,
                    readyVersionId.Value,
                    assetData.SearchRevision,
                    canonical.ContentHash,
                    modelKey,
                    AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION);

                Guid jobId = await jobStore.Enqueue(
                    assetId,
                    readyVersionId.Value,
                    AssetProcessingJobType.EMBEDDING_GENERATION,
                    definitionVersion: 1,
                    initialDelay: TimeSpan.Zero,
                    payload,
                    traceParent: null,
                    cancellationToken);

                if (jobId != Guid.Empty)
                {
                    enqueuedCount++;
                }
            }

            if (eligibleAssetIds.Count < limit)
            {
                if (!hasWrapped && currentOffset > eligibleAssetIds.Count)
                {
                    hasWrapped = true;
                    currentOffset = 0;
                }
                else
                {
                    currentOffset = 0;
                    break;
                }
            }
        }

        await tx.CommitAsync(cancellationToken);

        // Best-effort cursor advance after successful DB transaction commit.
        // Cache write failure must not throw or alter the committed cycle outcome.
        try
        {
            await SetCursorOffset(modelKey, currentOffset, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to persist backfill cursor after transaction commit");
        }

        var outcome = enqueuedCount > 0 ? EmbeddingBackfillOutcomes.SUCCESS : EmbeddingBackfillOutcomes.NO_CANDIDATES;
        AssetBlockDiagnostics.RecordEmbeddingBackfillCycle(stopwatch.Elapsed, enqueuedCount, outcome);
        return enqueuedCount;
    }
}
