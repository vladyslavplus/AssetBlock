using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Infrastructure.Persistence.Configurations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class CheckoutIntentStore(ApplicationDbContext dbContext) : ICheckoutIntentStore
{
    private const string PENDING_ASSET_UNIQUE = "UIX_checkout_intents_user_asset_pending";
    private const string PENDING_BUNDLE_UNIQUE = "UIX_checkout_intents_user_bundle_pending";

    public async Task CreateWithItemsAndReservations(
        CheckoutIntent intent,
        IReadOnlyList<CheckoutIntentItem> items,
        IReadOnlyList<CheckoutReservation> reservations,
        CancellationToken cancellationToken = default)
    {
        try
        {
            dbContext.CheckoutIntents.Add(intent);
            dbContext.CheckoutIntentItems.AddRange(items);
            dbContext.CheckoutReservations.AddRange(reservations);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: CheckoutReservationConfiguration.UNIQUE_USER_ASSET
            })
        {
            DetachGraph(intent, items, reservations);
            throw new CheckoutItemReservedException();
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: PENDING_ASSET_UNIQUE or PENDING_BUNDLE_UNIQUE
            })
        {
            DetachGraph(intent, items, reservations);
            throw new ActiveCheckoutIntentException();
        }
    }

    public Task<CheckoutIntent?> GetPendingForAsset(Guid userId, Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.CheckoutIntents
            .AsNoTracking()
            .Include(i => i.Items)
            .FirstOrDefaultAsync(
                i => i.UserId == userId
                     && i.AssetId == assetId
                     && i.Status == CheckoutIntentStatus.PENDING,
                cancellationToken);
    }

    public Task<CheckoutIntent?> GetPendingForBundle(Guid userId, Guid bundleId, CancellationToken cancellationToken = default)
    {
        return dbContext.CheckoutIntents
            .AsNoTracking()
            .Include(i => i.Items)
            .FirstOrDefaultAsync(
                i => i.UserId == userId
                     && i.BundleId == bundleId
                     && i.Status == CheckoutIntentStatus.PENDING,
                cancellationToken);
    }

    public Task<CheckoutIntent?> GetByIdWithItems(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.CheckoutIntents
            .AsNoTracking()
            .Include(i => i.Items)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    public async Task ReleaseExpiredReservations(
        Guid userId,
        IReadOnlyList<Guid> assetIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (assetIds.Count == 0)
        {
            return;
        }

        // Only unattached locally-expired reservations. Attached Stripe sessions keep seats until Stripe expiry.
        await dbContext.CheckoutReservations
            .Where(r => r.UserId == userId
                        && assetIds.Contains(r.AssetId)
                        && r.ExpiresAt <= now
                        && r.CheckoutIntent.StripeSessionId == null)
            .ExecuteDeleteAsync(cancellationToken);
    }

    public async Task<bool> HasActiveForAsset(Guid assetId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        var hasPendingItem = await dbContext.CheckoutIntentItems
            .AsNoTracking()
            .AnyAsync(
                i => i.AssetId == assetId
                     && i.CheckoutIntent.Status == CheckoutIntentStatus.PENDING
                     && (i.CheckoutIntent.ExpiresAt > now || i.CheckoutIntent.StripeSessionId != null),
                cancellationToken);

        if (hasPendingItem)
        {
            return true;
        }

        return await dbContext.CheckoutReservations
            .AsNoTracking()
            .AnyAsync(
                r => r.AssetId == assetId
                     && (r.ExpiresAt > now || r.CheckoutIntent.StripeSessionId != null)
                     && r.CheckoutIntent.Status == CheckoutIntentStatus.PENDING,
                cancellationToken);
    }

    public async Task<bool> TryCancelAndRelease(Guid id, CancellationToken cancellationToken = default)
    {
        var updated = await dbContext.CheckoutIntents
            .Where(i => i.Id == id && i.Status == CheckoutIntentStatus.PENDING)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(i => i.Status, CheckoutIntentStatus.CANCELLED),
                cancellationToken);
        if (updated != 1)
        {
            return false;
        }

        await dbContext.CheckoutReservations
            .Where(r => r.CheckoutIntentId == id)
            .ExecuteDeleteAsync(cancellationToken);
        return true;
    }

    public async Task<bool> TrySetStripeSessionId(Guid id, string stripeSessionId, CancellationToken cancellationToken = default)
    {
        var updated = await dbContext.CheckoutIntents
            .Where(i => i.Id == id
                        && i.Status == CheckoutIntentStatus.PENDING
                        && (i.StripeSessionId == null || i.StripeSessionId == stripeSessionId))
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(i => i.StripeSessionId, stripeSessionId),
                cancellationToken);
        return updated == 1;
    }

    public async Task<bool> TryCompleteAndRelease(
        Guid id,
        Guid userId,
        string stripeSessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        // Do not gate on ExpiresAt: a paid Stripe session must fulfill even if the webhook is delayed.
        var updated = await dbContext.CheckoutIntents
            .Where(i => i.Id == id
                        && i.UserId == userId
                        && i.Status == CheckoutIntentStatus.PENDING
                        && (i.StripeSessionId == null || i.StripeSessionId == stripeSessionId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(i => i.StripeSessionId, stripeSessionId)
                    .SetProperty(i => i.Status, CheckoutIntentStatus.COMPLETED)
                    .SetProperty(i => i.CompletedAt, now),
                cancellationToken);
        if (updated != 1)
        {
            return false;
        }

        await dbContext.CheckoutReservations
            .Where(r => r.CheckoutIntentId == id)
            .ExecuteDeleteAsync(cancellationToken);
        return true;
    }

    public async Task<int> CleanupExpiredUnattachedPendingBatch(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
        {
            return 0;
        }

        await using var tx = await dbContext.Database.BeginTransactionAsync(cancellationToken);

        var rows = await dbContext.CheckoutIntents
            .FromSqlInterpolated($"""
                SELECT *
                FROM checkout_intents
                WHERE "Status" = 'PENDING'
                  AND "ExpiresAt" <= {now}
                  AND "StripeSessionId" IS NULL
                ORDER BY "ExpiresAt", "Id"
                FOR UPDATE SKIP LOCKED
                LIMIT {batchSize}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            await tx.CommitAsync(cancellationToken);
            return 0;
        }

        var ids = rows.Select(r => r.Id).ToList();
        await dbContext.CheckoutIntents
            .Where(i => ids.Contains(i.Id) && i.Status == CheckoutIntentStatus.PENDING)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(i => i.Status, CheckoutIntentStatus.CANCELLED),
                cancellationToken);

        await dbContext.CheckoutReservations
            .Where(r => ids.Contains(r.CheckoutIntentId))
            .ExecuteDeleteAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return ids.Count;
    }

    public async Task<IReadOnlyList<(Guid Id, string StripeSessionId)>> ListExpiredAttachedPendingBatch(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
        {
            return [];
        }

        var rows = await dbContext.CheckoutIntents
            .AsNoTracking()
            .Where(i => i.Status == CheckoutIntentStatus.PENDING
                        && i.ExpiresAt <= now
                        && i.StripeSessionId != null)
            .OrderBy(i => i.ExpiresAt)
            .ThenBy(i => i.Id)
            .Take(batchSize)
            .Select(i => new { i.Id, i.StripeSessionId })
            .ToListAsync(cancellationToken);

        return rows
            .Where(r => !string.IsNullOrWhiteSpace(r.StripeSessionId))
            .Select(r => (r.Id, r.StripeSessionId!))
            .ToList();
    }

    public async Task DeleteTerminalUnpaidReferencingAsset(Guid assetId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var intentIds = await dbContext.CheckoutIntentItems
            .AsNoTracking()
            .Where(i => i.AssetId == assetId)
            .Where(i =>
                i.CheckoutIntent.Order == null
                && (i.CheckoutIntent.Status == CheckoutIntentStatus.CANCELLED
                    || (i.CheckoutIntent.Status == CheckoutIntentStatus.PENDING
                        && i.CheckoutIntent.ExpiresAt <= now
                        && i.CheckoutIntent.StripeSessionId == null)))
            .Select(i => i.CheckoutIntentId)
            .Distinct()
            .ToListAsync(cancellationToken);

        if (intentIds.Count == 0)
        {
            return;
        }

        await dbContext.CheckoutIntents
            .Where(i => intentIds.Contains(i.Id))
            .ExecuteDeleteAsync(cancellationToken);
    }

    private void DetachGraph(
        CheckoutIntent intent,
        IReadOnlyList<CheckoutIntentItem> items,
        IReadOnlyList<CheckoutReservation> reservations)
    {
        dbContext.Entry(intent).State = EntityState.Detached;
        foreach (var item in items)
        {
            dbContext.Entry(item).State = EntityState.Detached;
        }

        foreach (var reservation in reservations)
        {
            dbContext.Entry(reservation).State = EntityState.Detached;
        }
    }
}
