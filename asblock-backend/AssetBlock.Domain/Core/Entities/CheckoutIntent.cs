using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Durable server-side checkout snapshot header. Product membership and pinned versions live on items.
/// </summary>
public class CheckoutIntent : BaseEntity
{
    public required Guid UserId { get; set; }

    /// <summary>Set for single-asset checkout; null for bundle checkout.</summary>
    public Guid? AssetId { get; set; }

    /// <summary>Set for bundle checkout; null for single-asset checkout.</summary>
    public Guid? BundleId { get; set; }

    /// <summary>Pinned bundle revision for bundle checkout.</summary>
    public Guid? BundleRevisionId { get; set; }

    public required string ProductTitle { get; set; }
    public required decimal AmountTotal { get; set; }
    public required string Currency { get; set; }
    public string? StripeSessionId { get; set; }
    public required CheckoutIntentStatus Status { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    /// <summary>
    /// Last successful server-side Stripe session poll for attached pending intents.
    /// Null means never reconciled; workers use CreatedAt as the backoff baseline.
    /// </summary>
    public DateTimeOffset? LastStripeReconciledAt { get; set; }

    /// <summary>
    /// Engagement attribution captured once when the intent is created. All attribution fields are
    /// optional and must never be rewritten afterwards, so a later session cannot re-attribute a sale.
    /// AttributionCollectionId intentionally has no foreign key to collections.
    /// </summary>
    public Guid? AnalyticsVisitorId { get; set; }

    public Guid? AnalyticsSessionId { get; set; }
    public AnalyticsTrafficSource? AttributionSource { get; set; }
    public Guid? AttributionCollectionId { get; set; }
    public string? AttributionReferrerHost { get; set; }

    public User User { get; set; } = null!;
    public Asset? Asset { get; set; }
    public Bundle? Bundle { get; set; }
    public BundleRevision? BundleRevision { get; set; }
    public ICollection<CheckoutIntentItem> Items { get; set; } = new List<CheckoutIntentItem>();
    public ICollection<CheckoutReservation> Reservations { get; set; } = new List<CheckoutReservation>();
    public Order? Order { get; set; }
}
