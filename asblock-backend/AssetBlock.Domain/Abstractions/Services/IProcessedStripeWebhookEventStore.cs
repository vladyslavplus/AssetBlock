namespace AssetBlock.Domain.Abstractions.Services;

public interface IProcessedStripeWebhookEventStore
{
    /// <summary>
    /// Atomically records a processed Stripe webhook event.
    /// Returns true if newly claimed; false if already processed (conflict).
    /// Must participate in the active database transaction.
    /// </summary>
    Task<bool> TryRecordEvent(
        string stripeEventId,
        string eventType,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken = default);
}
