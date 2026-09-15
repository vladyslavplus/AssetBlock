using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Opt-in personalization preferences and recomputed affinity snapshots.
/// All reads and writes are scoped to a single account UserId; anonymous
/// visitor/session identifiers never participate.
/// </summary>
public interface IRecommendationPersonalizationStore
{
    Task<UserRecommendationPreferences?> GetPreferences(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Upserts the opt-in flag. Turning off deletes the account's affinity rows
    /// in the same locked transaction (opt-off == delete).
    /// </summary>
    Task<UserRecommendationPreferences> SetPersonalized(
        Guid userId,
        bool personalized,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Full recompute for one opted-in user from source rows in the personalization window.
    /// Skips (returns false) when the account is not opted in at lock time.
    /// Atomic replace: exact totals upserted, absent rows deleted, in one locked transaction.
    /// </summary>
    Task<bool> TryRecomputeUser(Guid userId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Lists opted-in user ids for bounded worker batches, oldest recompute first.</summary>
    Task<IReadOnlyList<Guid>> ListOptedInUserIds(int batchSize, CancellationToken cancellationToken = default);

    /// <summary>
    /// Bounded worker batch: recomputes up to maxUsers opted-in accounts.
    /// Skipped entirely when another worker holds the scheduling lock.
    /// </summary>
    Task<PersonalRecomputeBatchResult> TryRecomputeBatch(
        DateTimeOffset now,
        int maxUsers,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Bounded direct read for ranking, race-free against concurrent opt-out:
    /// locks the preference row, returns null (Phase A fallback) unless still opted in.
    /// Pair clicks for (user, source, targets) and per-tag purchase/review totals
    /// for the candidate set's tags. Anonymous identifiers never participate.
    /// </summary>
    Task<PersonalAffinitySignals?> GetSignals(
        Guid userId,
        Guid sourceAssetId,
        IReadOnlyList<Guid> candidateIds,
        CancellationToken cancellationToken = default);
}
