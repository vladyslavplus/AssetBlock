using AssetBlock.Domain.Core.Dto.Payments;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Verified Stripe checkout.session.completed data (no DB writes).</summary>
public sealed record StripeCheckoutCompleted(
    Guid CheckoutIntentId,
    Guid UserId,
    Guid AssetId,
    Guid AssetVersionId,
    string StripeSessionId,
    decimal AmountTotal,
    string Currency);

/// <summary>
/// Creates checkout sessions and verifies payment webhooks (e.g., Stripe).
/// </summary>
public interface IPaymentService
{
    /// <summary>Creates a checkout session for the given line item.</summary>
    Task<StripeCheckoutSession> CreateCheckoutSession(CheckoutLineItem item, Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Retrieves an existing checkout session so an interrupted checkout can be resumed safely.</summary>
    Task<StripeCheckoutSessionSnapshot> GetCheckoutSession(string stripeSessionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Verifies webhook signature and extracts paid checkout session metadata.
    /// Returns null for ignored events. Does not create purchases.
    /// Throws <see cref="AssetBlock.Domain.Core.Exceptions.StripeWebhookInvalidSignatureException"/> on signature failure.
    /// </summary>
    Task<StripeCheckoutCompleted?> VerifyCheckoutCompleted(string payload, string signature, CancellationToken cancellationToken = default);
}

public sealed record StripeCheckoutSession(string Id, string Url);

public sealed record StripeCheckoutSessionSnapshot(string Id, string Status, string? Url);
