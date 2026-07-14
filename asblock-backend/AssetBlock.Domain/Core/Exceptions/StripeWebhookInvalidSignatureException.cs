namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Thrown when a Stripe webhook payload fails signature verification.</summary>
public sealed class StripeWebhookInvalidSignatureException()
    : Exception("Stripe webhook signature validation failed.");
