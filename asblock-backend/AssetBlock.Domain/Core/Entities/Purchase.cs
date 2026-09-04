using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Per-asset download entitlement after successful payment. Historical price lives on OrderLine/Order.
/// </summary>
public class Purchase : BaseEntity
{
    public required Guid UserId { get; set; }
    public required Guid AssetId { get; set; }

    /// <summary>Exact AssetVersion sold at checkout.</summary>
    public required Guid AssetVersionId { get; set; }

    /// <summary>Paid order line that granted this entitlement.</summary>
    public required Guid OrderLineId { get; set; }

    public DateTimeOffset PurchasedAt { get; init; }

    public User User { get; set; } = null!;
    public Asset Asset { get; set; } = null!;
    public AssetVersion AssetVersion { get; set; } = null!;
    public OrderLine OrderLine { get; set; } = null!;
}
