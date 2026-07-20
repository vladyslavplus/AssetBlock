using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Records a user's right to download an asset after successful payment.
/// </summary>
public class Purchase : BaseEntity
{
    public required Guid UserId { get; set; }
    public required Guid AssetId { get; set; }
    /// <summary>Exact AssetVersion sold at checkout.</summary>
    public required Guid AssetVersionId { get; set; }
    /// <summary>Durable checkout snapshot that led to this purchase.</summary>
    public required Guid CheckoutIntentId { get; set; }
    /// <summary>Stripe Checkout Session ID for idempotency and lookup.</summary>
    public required string StripePaymentId { get; set; }
    /// <summary>Amount actually paid at checkout.</summary>
    public required decimal PricePaid { get; set; }
    /// <summary>ISO currency code (lowercase).</summary>
    public required string Currency { get; set; }
    public DateTimeOffset PurchasedAt { get; init; } = DateTimeOffset.UtcNow;

    public User User { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
    public AssetVersion AssetVersion { get; set; } = null!;
    public CheckoutIntent CheckoutIntent { get; set; } = null!;
}
