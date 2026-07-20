using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class CheckoutIntentStore(ApplicationDbContext dbContext) : ICheckoutIntentStore
{
    private const string PENDING_UNIQUE_INDEX = "UIX_checkout_intents_user_asset_pending";

    public async Task Create(CheckoutIntent intent, CancellationToken cancellationToken = default)
    {
        try
        {
            dbContext.CheckoutIntents.Add(intent);
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: PENDING_UNIQUE_INDEX
            })
        {
            dbContext.Entry(intent).State = EntityState.Detached;
            throw new ActiveCheckoutIntentException();
        }
    }

    public Task CancelExpiredPending(Guid userId, Guid assetId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        return dbContext.CheckoutIntents
            .Where(i => i.UserId == userId
                        && i.AssetId == assetId
                        && i.Status == CheckoutIntentStatus.PENDING
                        && i.ExpiresAt <= now)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(i => i.Status, CheckoutIntentStatus.CANCELLED),
                cancellationToken);
    }

    public Task<CheckoutIntent?> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.CheckoutIntents
            .AsNoTracking()
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);
    }

    public Task<bool> HasActiveForAsset(Guid assetId, DateTimeOffset now, CancellationToken cancellationToken = default)
    {
        return dbContext.CheckoutIntents
            .AsNoTracking()
            .AnyAsync(i => i.AssetId == assetId && i.Status == CheckoutIntentStatus.PENDING && i.ExpiresAt > now, cancellationToken);
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

    public async Task<bool> TryComplete(
        Guid id,
        Guid userId,
        Guid assetId,
        Guid assetVersionId,
        string stripeSessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var updated = await dbContext.CheckoutIntents
            .Where(i => i.Id == id
                        && i.UserId == userId
                        && i.AssetId == assetId
                        && i.AssetVersionId == assetVersionId
                        && i.Status == CheckoutIntentStatus.PENDING
                        && i.ExpiresAt > now
                        && (i.StripeSessionId == null || i.StripeSessionId == stripeSessionId))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(i => i.StripeSessionId, stripeSessionId)
                    .SetProperty(i => i.Status, CheckoutIntentStatus.COMPLETED)
                    .SetProperty(i => i.CompletedAt, now),
                cancellationToken);
        return updated == 1;
    }
}
