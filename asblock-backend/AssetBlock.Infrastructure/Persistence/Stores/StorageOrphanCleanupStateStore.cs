using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.Storage;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class StorageOrphanCleanupStateStore(ApplicationDbContext dbContext) : IStorageOrphanCleanupStateStore
{
    private const int STATE_ID = 1;

    public async Task<StorageOrphanCleanupLease?> TryAcquire(
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        await dbContext.Database.ExecuteSqlInterpolatedAsync(
            $"""INSERT INTO storage_orphan_cleanup_states ("Id") VALUES ({STATE_ID}) ON CONFLICT ("Id") DO NOTHING""",
            cancellationToken).ConfigureAwait(false);

        var leaseId = Guid.NewGuid();
        var acquired = await dbContext.Set<StorageOrphanCleanupState>()
            .Where(state => state.Id == STATE_ID && (state.LeaseUntil == null || state.LeaseUntil <= now))
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(state => state.LeaseId, leaseId)
                .SetProperty(state => state.LeaseUntil, now.Add(leaseDuration)), cancellationToken)
            .ConfigureAwait(false);
        if (acquired == 0)
        {
            return null;
        }

        StorageOrphanCleanupState state = await dbContext.Set<StorageOrphanCleanupState>()
            .AsNoTracking()
            .SingleAsync(state => state.Id == STATE_ID && state.LeaseId == leaseId, cancellationToken)
            .ConfigureAwait(false);
        StorageObjectCursor? cursor = state.CursorKey is null
            ? null
            : new StorageObjectCursor(state.CursorKey, state.CursorVersionId);
        return new StorageOrphanCleanupLease(leaseId, cursor);
    }

    public async Task<bool> SaveCursor(
        Guid leaseId,
        StorageObjectCursor? cursor,
        DateTimeOffset now,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        var updated = await dbContext.Set<StorageOrphanCleanupState>()
            .Where(state => state.Id == STATE_ID && state.LeaseId == leaseId && state.LeaseUntil > now)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(state => state.CursorKey, cursor?.Key)
                .SetProperty(state => state.CursorVersionId, cursor?.VersionId)
                .SetProperty(state => state.LeaseUntil, now.Add(leaseDuration)), cancellationToken)
            .ConfigureAwait(false);
        return updated == 1;
    }

    public async Task Release(Guid leaseId, bool resetCursor, CancellationToken cancellationToken = default)
    {
        IQueryable<StorageOrphanCleanupState> query = dbContext.Set<StorageOrphanCleanupState>()
            .Where(state => state.Id == STATE_ID && state.LeaseId == leaseId);
        await query.ExecuteUpdateAsync(setters => setters
            .SetProperty(state => state.LeaseId, (Guid?)null)
            .SetProperty(state => state.LeaseUntil, (DateTimeOffset?)null)
            .SetProperty(state => state.CursorKey, state => resetCursor ? null : state.CursorKey)
            .SetProperty(state => state.CursorVersionId, state => resetCursor ? null : state.CursorVersionId), cancellationToken)
            .ConfigureAwait(false);
    }
}
