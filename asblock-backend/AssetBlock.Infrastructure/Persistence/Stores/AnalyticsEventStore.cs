using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Analytics;
using Microsoft.EntityFrameworkCore;
using System.Data;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class AnalyticsEventStore(ApplicationDbContext dbContext) : IAnalyticsEventStore
{
    public async Task<bool> TryInsert(AnalyticsEvent analyticsEvent, CancellationToken cancellationToken = default)
    {
        var eventType = analyticsEvent.EventType.ToString();
        var source = analyticsEvent.Source.ToString();
        var deviceClass = analyticsEvent.DeviceClass.ToString();

        // Beacons are retried by the browser, so a replayed client Id must be a no-op rather than a conflict.
        var inserted = await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO analytics_events (
                "Id", "EventType", "OccurredAt", "SellerId", "VisitorId", "SessionId", "ActorUserId",
                "AssetId", "AssetVersionId", "BundleId", "CollectionId", "Source", "ReferrerHost", "DeviceClass")
            VALUES (
                {analyticsEvent.Id}, {eventType}, {analyticsEvent.OccurredAt}, {analyticsEvent.SellerId},
                {analyticsEvent.VisitorId}, {analyticsEvent.SessionId}, {analyticsEvent.ActorUserId}::uuid,
                {analyticsEvent.AssetId}::uuid, {analyticsEvent.AssetVersionId}::uuid,
                {analyticsEvent.BundleId}::uuid, {analyticsEvent.CollectionId}::uuid,
                {source}, {analyticsEvent.ReferrerHost}, {deviceClass})
            ON CONFLICT ("Id") DO NOTHING
            """,
            cancellationToken);

        return inserted == 1;
    }

    public async Task<AnalyticsDailyRecomputeResult> TryAcquireAndRecomputeDaily(
        DateOnly dayUtc,
        DateOnly previousDayUtc,
        DateTimeOffset updatedAt,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        dbContext.Database.SetCommandTimeout(commandTimeoutSeconds);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        var lockAcquired = await dbContext.Database
            .SqlQueryRaw<bool>(
                $"SELECT pg_try_advisory_xact_lock({AnalyticsAggregationConstants.DAILY_ROLLUP_ADVISORY_LOCK_KEY}) AS \"Value\"")
            .SingleAsync(cancellationToken);

        if (!lockAcquired)
        {
            await transaction.RollbackAsync(cancellationToken);
            return new AnalyticsDailyRecomputeResult(AnalyticsDailyRecomputeOutcome.SKIPPED, 0, 0, 0, 0);
        }

        var sellerRows = 0;
        var productRows = 0;
        var collectionRows = 0;
        var trafficRows = 0;

        foreach (var day in new[] { previousDayUtc, dayUtc })
        {
            var dayStart = ToDayStartUtc(day);
            var dayEnd = dayStart.AddDays(1);

            sellerRows += await dbContext.Database.ExecuteSqlRawAsync(
                AnalyticsDailyRollupSql.UPSERT_SELLER_DAILY,
                [dayStart, dayEnd, day, updatedAt],
                cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync(
                AnalyticsDailyRollupSql.DELETE_STALE_SELLER_DAILY,
                [dayStart, dayEnd, day],
                cancellationToken);

            productRows += await dbContext.Database.ExecuteSqlRawAsync(
                AnalyticsDailyRollupSql.UPSERT_PRODUCT_DAILY,
                [dayStart, dayEnd, day, updatedAt],
                cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync(
                AnalyticsDailyRollupSql.DELETE_STALE_PRODUCT_DAILY,
                [dayStart, dayEnd, day],
                cancellationToken);

            collectionRows += await dbContext.Database.ExecuteSqlRawAsync(
                AnalyticsDailyRollupSql.UPSERT_COLLECTION_DAILY,
                [dayStart, dayEnd, day, updatedAt],
                cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync(
                AnalyticsDailyRollupSql.DELETE_STALE_COLLECTION_DAILY,
                [dayStart, dayEnd, day],
                cancellationToken);

            trafficRows += await dbContext.Database.ExecuteSqlRawAsync(
                AnalyticsDailyRollupSql.UPSERT_TRAFFIC_DAILY,
                [dayStart, dayEnd, day, updatedAt],
                cancellationToken);
            await dbContext.Database.ExecuteSqlRawAsync(
                AnalyticsDailyRollupSql.DELETE_STALE_TRAFFIC_DAILY,
                [dayStart, dayEnd, day],
                cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        return new AnalyticsDailyRecomputeResult(
            AnalyticsDailyRecomputeOutcome.COMPLETED,
            sellerRows,
            productRows,
            collectionRows,
            trafficRows);
    }

    public async Task<AnalyticsEventRetentionResult> TryAcquireAndDeleteExpiredEvents(
        DateTimeOffset cutoffExclusive,
        int batchSize,
        int maxBatches,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        dbContext.Database.SetCommandTimeout(commandTimeoutSeconds);

        await using var transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.RepeatableRead,
            cancellationToken);

        var lockAcquired = await dbContext.Database
            .SqlQueryRaw<bool>(
                $"SELECT pg_try_advisory_xact_lock({AnalyticsAggregationConstants.RETENTION_ADVISORY_LOCK_KEY}) AS \"Value\"")
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
                AnalyticsDailyRollupSql.DELETE_EXPIRED_EVENTS_BATCH,
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
