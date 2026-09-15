using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Append-only recommendation measurement persistence and daily rollup maintenance.</summary>
public interface IRecommendationEventStore
{
    Task<bool> TryInsert(RecommendationEvent recommendationEvent, CancellationToken cancellationToken = default);

    Task<RecommendationDailyRecomputeResult> TryAcquireAndRecomputeDaily(
        DateOnly dayUtc,
        DateOnly previousDayUtc,
        DateTimeOffset updatedAt,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default);

    Task<AnalyticsEventRetentionResult> TryAcquireAndDeleteExpiredEvents(
        DateTimeOffset cutoffExclusive,
        int batchSize,
        int maxBatches,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default);
}
