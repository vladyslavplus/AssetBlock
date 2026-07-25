namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Durable (UserId, AssetId) reservation while a checkout intent is pending.
/// Prevents overlapping single-asset and bundle checkouts for the same buyer/asset.
/// </summary>
public class CheckoutReservation
{
    public required Guid Id { get; init; }
    public required Guid CheckoutIntentId { get; set; }
    public required Guid UserId { get; set; }
    public required Guid AssetId { get; set; }
    public required DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public CheckoutIntent CheckoutIntent { get; set; } = null!;
    public User User { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
}
