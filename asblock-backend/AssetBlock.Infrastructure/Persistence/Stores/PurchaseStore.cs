using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class PurchaseStore(ApplicationDbContext dbContext) : IPurchaseStore
{
    public async Task<Purchase> Add(Purchase purchase, CancellationToken cancellationToken = default)
    {
        dbContext.Purchases.Add(purchase);
        await dbContext.SaveChangesAsync(cancellationToken);
        return purchase;
    }

    public Task<bool> Exists(Guid userId, Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.Purchases
            .AsNoTracking()
            .AnyAsync(p => p.UserId == userId && p.AssetId == assetId, cancellationToken);
    }

    public Task<Purchase?> GetByStripePaymentId(string stripePaymentId, CancellationToken cancellationToken = default)
    {
        return dbContext.Purchases
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.StripePaymentId == stripePaymentId, cancellationToken);
    }

    public Task<Purchase?> GetPurchase(Guid userId, Guid assetId, CancellationToken cancellationToken = default)
    {
        return dbContext.Purchases
            .AsNoTracking()
            .FirstOrDefaultAsync(p => p.UserId == userId && p.AssetId == assetId, cancellationToken);
    }
}
