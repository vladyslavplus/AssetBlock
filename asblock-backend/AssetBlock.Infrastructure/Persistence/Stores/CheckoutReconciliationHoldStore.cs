using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class CheckoutReconciliationHoldStore(ApplicationDbContext dbContext) : ICheckoutReconciliationHoldStore
{
    public void StageHold(PaidCheckoutReconciliationHold hold) =>
        dbContext.PaidCheckoutReconciliationHolds.Add(hold);

    public async Task<bool> TryCreateHold(
        PaidCheckoutReconciliationHold hold,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrWhiteSpace(hold.StripeEventId))
        {
            var exists = await dbContext.PaidCheckoutReconciliationHolds
                .AsNoTracking()
                .AnyAsync(h => h.StripeEventId == hold.StripeEventId, cancellationToken);
            if (exists)
            {
                return false;
            }
        }

        dbContext.PaidCheckoutReconciliationHolds.Add(hold);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            return false;
        }
    }

    public Task<bool> HasUnresolvedHoldForAsset(Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.PaidCheckoutReconciliationHolds
            .AsNoTracking()
            .AnyAsync(
                h => h.State == PaidCheckoutReconciliationState.HELD
                    && h.CheckoutIntent.Items.Any(i => i.AssetId == assetId),
                cancellationToken);
    }

    public Task<bool> HasUnresolvedHoldForCheckoutIntent(
        Guid checkoutIntentId,
        CancellationToken cancellationToken = default)
    {
        return dbContext.PaidCheckoutReconciliationHolds
            .AsNoTracking()
            .AnyAsync(
                h => h.CheckoutIntentId == checkoutIntentId
                    && h.State == PaidCheckoutReconciliationState.HELD,
                cancellationToken);
    }
}
