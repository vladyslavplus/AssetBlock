using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Durable paid commerce header created by Stripe webhook fulfillment.
/// One order may contain one asset line or many bundle lines.
/// </summary>
public class Order : BaseEntity
{
    public required Guid UserId { get; set; }
    public required Guid CheckoutIntentId { get; set; }

    /// <summary>Set for single-asset orders; null for bundle orders.</summary>
    public Guid? AssetId { get; set; }

    /// <summary>Set for bundle orders; null for single-asset orders.</summary>
    public Guid? BundleId { get; set; }

    /// <summary>Pinned bundle revision for bundle orders.</summary>
    public Guid? BundleRevisionId { get; set; }

    public required string ProductTitle { get; set; }
    public required string StripeSessionId { get; set; }
    public required decimal AmountPaid { get; set; }
    public required string Currency { get; set; }
    public required DateTimeOffset PurchasedAt { get; set; }

    public User User { get; set; } = null!;
    public CheckoutIntent CheckoutIntent { get; set; } = null!;
    public Asset? Asset { get; set; }
    public Bundle? Bundle { get; set; }
    public BundleRevision? BundleRevision { get; set; }
    public ICollection<OrderLine> Lines { get; set; } = new List<OrderLine>();
}
