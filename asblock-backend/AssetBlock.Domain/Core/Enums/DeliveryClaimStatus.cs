namespace AssetBlock.Domain.Core.Enums;

public enum DeliveryClaimStatus
{
    CLAIMED = 0,
    ALREADY_DELIVERED = 1,
    CONCURRENT_CONFLICT = 2
}
