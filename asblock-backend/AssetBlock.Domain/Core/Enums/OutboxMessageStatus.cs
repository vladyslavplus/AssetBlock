namespace AssetBlock.Domain.Core.Enums;

public enum OutboxMessageStatus
{
    PENDING = 0,
    PROCESSED = 1,
    DEAD_LETTERED = 2
}
