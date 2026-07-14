namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Creates checkout sessions and handles payment completion (e.g., Stripe).
/// </summary>
public interface IPaymentService
{
    /// <summary>Creates a checkout session for the asset; returns the session URL for redirect.</summary>
    Task<string> CreateCheckoutSession(Guid assetId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Handles webhook payload (e.g., Stripe checkout.session.completed).
    /// Returns (UserId, AssetId) if payment succeeded and purchase was recorded; null for ignored/idempotent events.
    /// Throws <see cref="AssetBlock.Domain.Core.Exceptions.StripeWebhookInvalidSignatureException"/> on signature failure.
    /// </summary>
    Task<(Guid UserId, Guid AssetId)?> HandleCheckoutCompleted(string payload, string signature, CancellationToken cancellationToken = default);
}
