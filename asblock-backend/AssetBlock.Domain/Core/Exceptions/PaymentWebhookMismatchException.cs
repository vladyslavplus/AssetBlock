namespace AssetBlock.Domain.Core.Exceptions;

/// <summary>Thrown when a paid Stripe webhook payload does not match its pending checkout intent or referenced asset versions.</summary>
public sealed class PaymentWebhookMismatchException(string message)
    : Exception(message);
