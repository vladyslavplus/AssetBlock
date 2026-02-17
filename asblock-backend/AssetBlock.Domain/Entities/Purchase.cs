namespace AssetBlock.Domain.Entities;

/// <summary>
/// Records a user's right to download an asset after successful payment.
/// </summary>
public class Purchase
{
    public required Guid Id { get; init; }
    public required Guid UserId { get; set; }
    public required Guid AssetId { get; set; }
    /// <summary>Stripe PaymentIntent or Checkout Session ID for idempotency and lookup.</summary>
    public string? StripePaymentId { get; set; }
    public DateTime PurchasedAt { get; set; }

    public User User { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
}
