namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Verified Stripe checkout.session.completed data (no DB writes).</summary>
public sealed record StripeCheckoutCompleted(
    Guid UserId,
    Guid AssetId,
    string StripeSessionId);

/// <summary>
/// Creates checkout sessions and verifies payment webhooks (e.g., Stripe).
/// </summary>
public interface IPaymentService
{
    /// <summary>Creates a checkout session for the asset; returns the session URL for redirect.</summary>
    Task<string> CreateCheckoutSession(Guid assetId, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies webhook signature and extracts paid checkout session metadata.
    /// Returns null for ignored events. Does not create purchases.
    /// Throws <see cref="AssetBlock.Domain.Core.Exceptions.StripeWebhookInvalidSignatureException"/> on signature failure.
    /// </summary>
    Task<StripeCheckoutCompleted?> VerifyCheckoutCompleted(string payload, string signature, CancellationToken cancellationToken = default);
}
