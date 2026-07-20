using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Durable server-side checkout snapshot. Prevents a paid Stripe session from losing its
/// asset/version entitlement while its webhook is pending.
/// </summary>
public class CheckoutIntent : BaseEntity
{
    public required Guid UserId { get; set; }
    public required Guid AssetId { get; set; }
    public required Guid AssetVersionId { get; set; }
    public required string AssetTitle { get; set; }
    public required decimal UnitAmount { get; set; }
    public required string Currency { get; set; }
    public string? StripeSessionId { get; set; }
    public required CheckoutIntentStatus Status { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }

    public User User { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
    public AssetVersion AssetVersion { get; set; } = null!;
    public Purchase? Purchase { get; set; }
}
