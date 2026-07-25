using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface ICheckoutIntentStore
{
    /// <summary>
    /// Inserts checkout intent header, items, and reservations atomically within the caller's transaction.
    /// </summary>
    Task CreateWithItemsAndReservations(
        CheckoutIntent intent,
        IReadOnlyList<CheckoutIntentItem> items,
        IReadOnlyList<CheckoutReservation> reservations,
        CancellationToken cancellationToken = default);

    Task<CheckoutIntent?> GetPendingForAsset(Guid userId, Guid assetId, CancellationToken cancellationToken = default);

    Task<CheckoutIntent?> GetPendingForBundle(Guid userId, Guid bundleId, CancellationToken cancellationToken = default);

    Task<CheckoutIntent?> GetByIdWithItems(Guid id, CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases locally expired reservations that belong to unattached pending intents only
    /// (no Stripe session). Attached sessions keep reservations until Stripe reports expired.
    /// </summary>
    Task ReleaseExpiredReservations(
        Guid userId,
        IReadOnlyList<Guid> assetIds,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    Task<bool> HasActiveForAsset(Guid assetId, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<bool> TryCancelAndRelease(Guid id, CancellationToken cancellationToken = default);

    Task<bool> TrySetStripeSessionId(Guid id, string stripeSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Completes a pending intent when Stripe confirms payment. Does not reject late webhook delivery
    /// solely because local <c>ExpiresAt</c> has passed.
    /// </summary>
    Task<bool> TryCompleteAndRelease(
        Guid id,
        Guid userId,
        string stripeSessionId,
        DateTimeOffset now,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels locally expired pending intents that never attached a Stripe session.
    /// </summary>
    Task<int> CleanupExpiredUnattachedPendingBatch(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists locally expired pending intents that have a Stripe session id (for out-of-TX Stripe sync).
    /// </summary>
    Task<IReadOnlyList<(Guid Id, string StripeSessionId)>> ListExpiredAttachedPendingBatch(
        DateTimeOffset now,
        int batchSize,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Deletes CANCELLED or expired unpaid intents that reference the asset via items.
    /// Never deletes pending intents or intents linked to orders.
    /// </summary>
    Task DeleteTerminalUnpaidReferencingAsset(Guid assetId, CancellationToken cancellationToken = default);
}
