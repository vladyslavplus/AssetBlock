using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface ICheckoutIntentStore
{
    Task Create(CheckoutIntent intent, CancellationToken cancellationToken = default);
    Task<CheckoutIntent?> GetPending(Guid userId, Guid assetId, CancellationToken cancellationToken = default);
    Task<CheckoutIntent?> GetById(Guid id, CancellationToken cancellationToken = default);
    Task<bool> HasActiveForAsset(Guid assetId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<bool> TryCancel(Guid id, CancellationToken cancellationToken = default);
    Task<bool> TrySetStripeSessionId(Guid id, string stripeSessionId, CancellationToken cancellationToken = default);
    Task<bool> TryComplete(
        Guid id,
        Guid userId,
        Guid assetId,
        Guid assetVersionId,
        string stripeSessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);
}
