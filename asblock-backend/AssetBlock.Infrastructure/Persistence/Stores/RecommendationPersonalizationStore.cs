using System.Data;
using System.Data.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Storage;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class RecommendationPersonalizationStore(ApplicationDbContext dbContext) : IRecommendationPersonalizationStore
{
    public async Task<UserRecommendationPreferences?> GetPreferences(Guid userId, CancellationToken cancellationToken = default)
    {
        return await dbContext.UserRecommendationPreferences
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
    }

    public async Task<UserRecommendationPreferences> SetPersonalized(
        Guid userId,
        bool personalized,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Participates in the caller's transaction when one is ambient (request path),
        // otherwise opens a short owned transaction (worker/tests).
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using IDbContextTransaction? transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        try
        {
            UserRecommendationPreferences? preferences = await LockPreferencesAsync(userId, cancellationToken);
            if (preferences is null)
            {
                // Concurrent first-time opt-ins serialize on the PK: the loser waits,
                // then takes the winner's row via ON CONFLICT DO NOTHING (no exception,
                // no aborted ambient transaction).
                await dbContext.Database.ExecuteSqlAsync(
                    $"""
                    INSERT INTO "user_recommendation_preferences" ("UserId", "IsPersonalized", "OptedInAt", "UpdatedAt")
                    VALUES ({userId}, {personalized}, {(personalized ? now : (DateTimeOffset?)null)}, {now})
                    ON CONFLICT ("UserId") DO NOTHING
                    """,
                    cancellationToken);
                preferences = await LockPreferencesAsync(userId, cancellationToken);
            }

            if (preferences is null)
            {
                throw new InvalidOperationException("Recommendation preferences row vanished mid-transaction.");
            }

            if (preferences.IsPersonalized != personalized || personalized)
            {
                preferences.IsPersonalized = personalized;
                preferences.OptedInAt = personalized ? preferences.OptedInAt ?? now : null;
                preferences.UpdatedAt = now;
                await dbContext.SaveChangesAsync(cancellationToken);
            }

            if (!personalized)
            {
                await DeleteUserAffinityAsync(userId, cancellationToken);
            }

            if (ownsTransaction)
            {
                await transaction!.CommitAsync(cancellationToken);
            }

            return preferences;
        }
        catch
        {
            if (ownsTransaction && transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<bool> TryRecomputeUser(Guid userId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var ownsTransaction = dbContext.Database.CurrentTransaction is null;
        await using IDbContextTransaction? transaction = ownsTransaction
            ? await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken)
            : null;

        try
        {
            UserRecommendationPreferences? preferences = await LockPreferencesAsync(userId, cancellationToken);
            if (preferences is null || !preferences.IsPersonalized)
            {
                if (ownsTransaction)
                {
                    await transaction!.RollbackAsync(cancellationToken);
                }

                return false;
            }

            // Atomic condition, re-checked under lock: opted-in window only, never backfilled.
            DateTimeOffset retentionStart = now.AddDays(-RecommendationTelemetryConstants.RAW_EVENT_RETENTION_DAYS);
            DateTimeOffset windowStart = preferences.OptedInAt is { } optedInAt && optedInAt > retentionStart
                ? optedInAt
                : retentionStart;

            var clicks = await dbContext.RecommendationEvents.AsNoTracking()
                .Where(e => e.ActorUserId == userId
                    && e.EventType == RecommendationEventType.CLICK
                    && e.OccurredAt >= windowStart)
                .GroupBy(e => new { e.SourceAssetId, e.TargetAssetId })
                .Select(g => new
                {
                    g.Key.SourceAssetId,
                    g.Key.TargetAssetId,
                    Clicks = g.LongCount(),
                    LastClickedAt = g.Max(e => e.OccurredAt)
                })
                .ToListAsync(cancellationToken);

            var purchaseTags = await (
                    from p in dbContext.Purchases.AsNoTracking()
                    join at in dbContext.AssetTags.AsNoTracking() on p.AssetId equals at.AssetId
                    where p.UserId == userId && p.PurchasedAt >= windowStart
                    group at by at.TagId into g
                    select new { TagId = g.Key, Count = g.LongCount() })
                .ToListAsync(cancellationToken);

            var reviewTags = await (
                    from r in dbContext.Reviews.AsNoTracking()
                    join at in dbContext.AssetTags.AsNoTracking() on r.AssetId equals at.AssetId
                    where r.UserId == userId && r.CreatedAt >= windowStart
                    group at by at.TagId into g
                    select new { TagId = g.Key, Count = g.LongCount() })
                .ToListAsync(cancellationToken);

            var tagTotals = new Dictionary<Guid, (long Purchases, long Reviews)>();
            foreach (var row in purchaseTags)
            {
                tagTotals[row.TagId] = (row.Count, 0);
            }

            foreach (var row in reviewTags)
            {
                tagTotals.TryGetValue(row.TagId, out (long Purchases, long Reviews) existing);
                tagTotals[row.TagId] = (existing.Purchases, existing.Reviews + row.Count);
            }

            // Atomic replace: exact totals in, absent rows out, in the locked transaction.
            await DeleteUserAffinityAsync(userId, cancellationToken);
            DetachTrackedAffinity(userId);

            if (tagTotals.Count > 0)
            {
                dbContext.UserTagAffinities.AddRange(tagTotals.Select(kv => new UserTagAffinity
                {
                    UserId = userId,
                    TagId = kv.Key,
                    Purchases = kv.Value.Purchases,
                    Reviews = kv.Value.Reviews,
                    UpdatedAt = now
                }));
            }

            if (clicks.Count > 0)
            {
                dbContext.UserSourceClickAffinities.AddRange(clicks.Select(c => new UserSourceClickAffinity
                {
                    UserId = userId,
                    SourceAssetId = c.SourceAssetId,
                    TargetAssetId = c.TargetAssetId,
                    Clicks = c.Clicks,
                    LastClickedAt = c.LastClickedAt,
                    UpdatedAt = now
                }));
            }

            preferences.UpdatedAt = now;
            await dbContext.SaveChangesAsync(cancellationToken);

            if (ownsTransaction)
            {
                await transaction!.CommitAsync(cancellationToken);
            }

            return true;
        }
        catch
        {
            if (ownsTransaction && transaction is not null)
            {
                await transaction.RollbackAsync(CancellationToken.None);
            }

            throw;
        }
    }

    public async Task<IReadOnlyList<Guid>> ListOptedInUserIds(int batchSize, CancellationToken cancellationToken = default)
    {
        return await dbContext.UserRecommendationPreferences.AsNoTracking()
            .Where(p => p.IsPersonalized)
            .OrderBy(p => p.UpdatedAt)
            .ThenBy(p => p.UserId)
            .Take(batchSize)
            .Select(p => p.UserId)
            .ToListAsync(cancellationToken);
    }

    public async Task<PersonalRecomputeBatchResult> TryRecomputeBatch(
        DateTimeOffset now,
        int maxUsers,
        int commandTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        dbContext.Database.SetCommandTimeout(commandTimeoutSeconds);

        // Session-level advisory locks are bound to the physical connection:
        // pin one connection for acquire, batch, and unlock so the lock cannot
        // leak onto a pooled connection or be released from a different one.
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
                    $"SELECT pg_try_advisory_lock({RecommendationTelemetryConstants.PERSONAL_RECOMPUTE_ADVISORY_LOCK_KEY}) AS \"Value\"")
                .SingleAsync(cancellationToken);

            if (!lockAcquired)
            {
                return new PersonalRecomputeBatchResult(AnalyticsDailyRecomputeOutcome.SKIPPED, 0);
            }

            IReadOnlyList<Guid> userIds = await ListOptedInUserIds(maxUsers, cancellationToken);
            var recomputed = 0;
            foreach (Guid userId in userIds)
            {
                if (await TryRecomputeUser(userId, now, cancellationToken))
                {
                    recomputed++;
                }
            }

            return new PersonalRecomputeBatchResult(AnalyticsDailyRecomputeOutcome.COMPLETED, recomputed);
        }
        finally
        {
            if (lockAcquired)
            {
                try
                {
                    await dbContext.Database.ExecuteSqlRawAsync(
                        $"SELECT pg_advisory_unlock({RecommendationTelemetryConstants.PERSONAL_RECOMPUTE_ADVISORY_LOCK_KEY})",
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

    public async Task<PersonalAffinitySignals?> GetSignals(
        Guid userId,
        Guid sourceAssetId,
        IReadOnlyList<Guid> candidateIds,
        CancellationToken cancellationToken = default)
    {
        if (candidateIds.Count == 0)
        {
            return null;
        }

        await using IDbContextTransaction transaction = await dbContext.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);

        try
        {
            UserRecommendationPreferences? preferences = await LockPreferencesAsync(userId, cancellationToken);
            if (preferences is null || !preferences.IsPersonalized)
            {
                await transaction.RollbackAsync(cancellationToken);
                return null;
            }

            var pairs = await dbContext.UserSourceClickAffinities.AsNoTracking()
                .Where(a => a.UserId == userId
                    && a.SourceAssetId == sourceAssetId
                    && candidateIds.Contains(a.TargetAssetId))
                .Select(a => new { a.TargetAssetId, a.Clicks })
                .ToListAsync(cancellationToken);

            var tagCounters = await dbContext.UserTagAffinities.AsNoTracking()
                .Where(a => a.UserId == userId)
                .Select(a => new { a.TagId, a.Purchases, a.Reviews })
                .ToListAsync(cancellationToken);

            var candidateTags = await dbContext.AssetTags.AsNoTracking()
                .Where(at => candidateIds.Contains(at.AssetId))
                .Select(at => new { at.AssetId, at.TagId })
                .ToListAsync(cancellationToken);

            await transaction.CommitAsync(cancellationToken);

            var tagTotals = tagCounters.ToDictionary(t => t.TagId, t => t.Purchases + t.Reviews);
            var tagScoreByCandidate = candidateTags
                .GroupBy(t => t.AssetId)
                .ToDictionary(
                    g => g.Key,
                    g => g.Select(t => t.TagId).Distinct().Sum(tagId => tagTotals.GetValueOrDefault(tagId)));

            return new PersonalAffinitySignals(
                pairs.ToDictionary(p => p.TargetAssetId, p => p.Clicks),
                tagScoreByCandidate);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None);
            throw;
        }
    }

    private async Task<UserRecommendationPreferences?> LockPreferencesAsync(Guid userId, CancellationToken cancellationToken)
    {
        return await dbContext.UserRecommendationPreferences
            .FromSqlRaw(
                """SELECT * FROM "user_recommendation_preferences" WHERE "UserId" = {0} FOR UPDATE""",
                userId)
            .FirstOrDefaultAsync(cancellationToken);
    }

    private async Task DeleteUserAffinityAsync(Guid userId, CancellationToken cancellationToken)
    {
        await dbContext.Database.ExecuteSqlAsync(
            $"""DELETE FROM "user_tag_affinity" WHERE "UserId" = {userId}""",
            cancellationToken);
        await dbContext.Database.ExecuteSqlAsync(
            $"""DELETE FROM "user_source_click_affinity" WHERE "UserId" = {userId}""",
            cancellationToken);
    }

    private void DetachTrackedAffinity(Guid userId)
    {
        // Raw DELETE bypasses the change tracker; detach stale instances so re-inserted
        // rows with the same keys do not conflict when the caller reuses this context.
        foreach (EntityEntry<UserTagAffinity> entry in dbContext.ChangeTracker.Entries<UserTagAffinity>().Where(e => e.Entity.UserId == userId).ToList())
        {
            entry.State = EntityState.Detached;
        }

        foreach (EntityEntry<UserSourceClickAffinity> entry in dbContext.ChangeTracker.Entries<UserSourceClickAffinity>().Where(e => e.Entity.UserId == userId).ToList())
        {
            entry.State = EntityState.Detached;
        }
    }
}
