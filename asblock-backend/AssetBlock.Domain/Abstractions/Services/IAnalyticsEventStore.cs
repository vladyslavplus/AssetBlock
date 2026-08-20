using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Append-only engagement telemetry persistence and daily rollup maintenance.</summary>
public interface IAnalyticsEventStore
{
    /// <summary>
    /// Inserts the event and ignores replays of an already stored client-supplied Id.
    /// Returns true when a new row was written.
    /// </summary>
    Task<bool> TryInsert(AnalyticsEvent analyticsEvent, CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts a transaction-scoped advisory lock and recomputes replacement daily totals for
    /// <paramref name="dayUtc"/> and <paramref name="previousDayUtc"/> from raw events.
    /// Returns <see cref="AnalyticsDailyRecomputeOutcome.SKIPPED"/> when the lock is not acquired.
    /// </summary>
    Task<AnalyticsDailyRecomputeResult> TryAcquireAndRecomputeDaily(
        DateOnly dayUtc,
        DateOnly previousDayUtc,
        DateTimeOffset updatedAt,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Attempts a transaction-scoped advisory lock and deletes raw events with
    /// <c>OccurredAt</c> strictly before <paramref name="cutoffExclusive"/> in up to
    /// <paramref name="maxBatches"/> bounded batches. Returns skipped when the lock is not acquired.
    /// Daily rollups are untouched.
    /// </summary>
    Task<AnalyticsEventRetentionResult> TryAcquireAndDeleteExpiredEvents(
        DateTimeOffset cutoffExclusive,
        int batchSize,
        int maxBatches,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default);
}
