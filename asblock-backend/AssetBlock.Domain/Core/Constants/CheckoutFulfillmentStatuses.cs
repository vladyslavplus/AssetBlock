namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Domain and API contract representation for checkout intent fulfillment status.
/// </summary>
public static class CheckoutFulfillmentStatuses
{
    public const string PENDING = "pending";
    public const string COMPLETED = "completed";
    public const string CANCELLED = "cancelled";
}
