using AssetBlock.Domain.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IPurchaseStore
{
    Task<Purchase> Add(Purchase purchase, CancellationToken cancellationToken = default);
    Task<bool> Exists(Guid userId, Guid assetId, CancellationToken cancellationToken = default);
    Task<Purchase?> GetByStripePaymentId(string stripePaymentId, CancellationToken cancellationToken = default);
}
