using System.Data;
using System.Data.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Recommendations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class RecommendationEventStore(ApplicationDbContext dbContext) : IRecommendationEventStore
{
    public async Task<bool> TryInsert(RecommendationEvent recommendationEvent, CancellationToken cancellationToken = default)
    {
        var eventType = recommendationEvent.EventType.ToString();
        var deviceClass = recommendationEvent.DeviceClass.ToString();

        try
        {
            var inserted = await dbContext.Database.ExecuteSqlAsync(
                $"""
                INSERT INTO recommendation_events (
                    "Id", "EventType", "OccurredAt", "ExposureId", "SourceAssetId", "TargetAssetId",
                    "SlotPosition", "RankingVersion", "VisitorId", "SessionId", "ActorUserId", "DeviceClass")
                VALUES (
                    {recommendationEvent.Id}, {eventType}, {recommendationEvent.OccurredAt},
                    {recommendationEvent.ExposureId}, {recommendationEvent.SourceAssetId},
                    {recommendationEvent.TargetAssetId}, {recommendationEvent.SlotPosition},
                    {recommendationEvent.RankingVersion}, {recommendationEvent.VisitorId},
                    {recommendationEvent.SessionId}, {recommendationEvent.ActorUserId}::uuid, {deviceClass})
                ON CONFLICT ("Id") DO NOTHING
                """,
                cancellationToken);

            return inserted == 1;
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.UniqueViolation)
        {
            return false;
        }
    }

    public async Task<RecommendationDailyRecomputeResult> TryAcquireAndRecomputeDaily(
        DateOnly dayUtc,
        DateOnly previousDayUtc,
        DateTimeOffset updatedAt,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        dbContext.Database.SetCommandTimeout(commandTimeoutSeconds);

        DbConnection connection = dbContext.Database.GetDbConnection();
        var openedConnection = false;
        if (connection.State != ConnectionState.Open)
        {
            await dbContext.Database.OpenConnectionAsync(cancellationToken);
            openedConnection = true;
        }

        var lockAcquired = false;
        try
        {
            lockAcquired = await dbContext.Database
                .SqlQueryRaw<bool>(
                    $"SELECT pg_try_advisory_lock({RecommendationTelemetryConstants.DAILY_ROLLUP_ADVISORY_LOCK_KEY}) AS \"Value\"")
                .SingleAsync(cancellationToken);

            if (!lockAcquired)
            {
                return new RecommendationDailyRecomputeResult(AnalyticsDailyRecomputeOutcome.SKIPPED, 0);
            }

            var rows = 0;
            foreach (DateOnly day in new[] { previousDayUtc, dayUtc })
            {
                DateTimeOffset dayStart = ToDayStartUtc(day);
                DateTimeOffset dayEnd = dayStart.AddDays(1);

                await using IDbContextTransaction tx = await dbContext.Database.BeginTransactionAsync(
                    IsolationLevel.ReadCommitted,
                    cancellationToken);
                rows += await dbContext.Database.ExecuteSqlRawAsync(
                    RecommendationDailyRollupSql.UPSERT_DAILY,
                    [dayStart, dayEnd, day, updatedAt],
                    cancellationToken);
                await dbContext.Database.ExecuteSqlRawAsync(
                    RecommendationDailyRollupSql.DELETE_STALE_DAILY,
                    [dayStart, dayEnd, day],
                    cancellationToken);
                await tx.CommitAsync(cancellationToken);
            }

            return new RecommendationDailyRecomputeResult(AnalyticsDailyRecomputeOutcome.COMPLETED, rows);
        }
        finally
        {
            if (lockAcquired)
            {
                try
                {
                    await dbContext.Database.ExecuteSqlRawAsync(
                        $"SELECT pg_advisory_unlock({RecommendationTelemetryConstants.DAILY_ROLLUP_ADVISORY_LOCK_KEY})",
                        CancellationToken.None);
                }
                catch
                {
                    // Suppress unlock failure during teardown
                }
            }

            if (openedConnection)
            {
                await dbContext.Database.CloseConnectionAsync();
            }
        }
    }

    public async Task<AnalyticsEventRetentionResult> TryAcquireAndDeleteExpiredEvents(
        DateTimeOffset cutoffExclusive,
        int batchSize,
        int maxBatches,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        dbContext.Database.SetCommandTimeout(commandTimeoutSeconds);

        await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        var lockAcquired = await dbContext.Database
            .SqlQueryRaw<bool>(
                $"SELECT pg_try_advisory_xact_lock({RecommendationTelemetryConstants.RETENTION_ADVISORY_LOCK_KEY}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        if (!lockAcquired)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AnalyticsEventRetentionResult(0, HasBacklog: false, LockAcquired: false);
        }

        var deletedTotal = 0;
        var hasBacklog = false;

        for (var batchIndex = 0; batchIndex < maxBatches; batchIndex++)
        {
            var deleted = await dbContext.Database.ExecuteSqlRawAsync(
                RecommendationDailyRollupSql.DELETE_EXPIRED_EVENTS_BATCH,
                [cutoffExclusive, batchSize],
                cancellationToken);

            deletedTotal += deleted;

            if (deleted < batchSize)
            {
                break;
            }

            if (batchIndex == maxBatches - 1)
            {
                hasBacklog = true;
            }
        }

        await transaction.CommitAsync(cancellationToken);
        return new AnalyticsEventRetentionResult(deletedTotal, hasBacklog, LockAcquired: true);
    }

    private static DateTimeOffset ToDayStartUtc(DateOnly dayUtc) =>
        new(dayUtc.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
}
