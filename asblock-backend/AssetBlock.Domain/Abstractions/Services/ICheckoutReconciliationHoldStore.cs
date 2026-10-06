using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface ICheckoutReconciliationHoldStore
{
    /// <summary>
    /// Inserts a durable paid fulfillment hold when checkout cannot grant entitlements safely.
    /// Dedupes on Stripe event id when provided.
    /// </summary>
    /// <summary>Stages a hold on the active DbContext; caller commits via unit of work.</summary>
    void StageHold(PaidCheckoutReconciliationHold hold);

    Task<bool> TryCreateHold(
        PaidCheckoutReconciliationHold hold,
        CancellationToken cancellationToken = default);

    Task<bool> HasUnresolvedHoldForAsset(Guid assetId, CancellationToken cancellationToken = default);

    Task<bool> HasUnresolvedHoldForCheckoutIntent(Guid checkoutIntentId, CancellationToken cancellationToken = default);
}
