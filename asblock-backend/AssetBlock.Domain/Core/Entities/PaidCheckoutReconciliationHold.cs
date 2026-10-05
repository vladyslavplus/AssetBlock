using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Durable record when payment succeeded but fulfillment cannot grant safe download entitlements.
/// </summary>
public class PaidCheckoutReconciliationHold : BaseEntity
{
    public required Guid CheckoutIntentId { get; init; }
    public string? StripeSessionId { get; init; }
    public string? StripeEventId { get; init; }
    public required PaidCheckoutReconciliationState State { get; set; }
    public required string SafePaymentFactsJson { get; init; }
    public required string ItemIdentitiesJson { get; init; }

    public CheckoutIntent CheckoutIntent { get; set; } = null!;
}
