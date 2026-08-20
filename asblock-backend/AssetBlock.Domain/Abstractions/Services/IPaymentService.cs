using AssetBlock.Domain.Core.Dto.Payments;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Creates checkout sessions and verifies payment webhooks (e.g., Stripe).
/// </summary>
public interface IPaymentService
{
    /// <summary>Creates a checkout session from a server-derived multi-line draft.</summary>
    Task<StripeCheckoutSession> CreateCheckoutSession(CheckoutSessionDraft draft, CancellationToken cancellationToken = default);

    /// <summary>Retrieves an existing checkout session so an interrupted checkout can be resumed safely.</summary>
    Task<StripeCheckoutSessionSnapshot> GetCheckoutSession(string stripeSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies webhook signature and extracts paid checkout session metadata.
    /// Returns null for ignored events. Does not create orders.
    /// Throws <see cref="AssetBlock.Domain.Core.Exceptions.StripeWebhookInvalidSignatureException"/> on signature failure.
    /// </summary>
    Task<StripeCheckoutCompleted?> VerifyCheckoutCompleted(string payload, string signature, CancellationToken cancellationToken = default);
}

public sealed record StripeCheckoutSession(string Id, string Url);

public sealed record StripeCheckoutSessionSnapshot(
    string Id,
    string Status,
    string? Url,
    StripeCheckoutCompleted? CompletedCheckout = null);
