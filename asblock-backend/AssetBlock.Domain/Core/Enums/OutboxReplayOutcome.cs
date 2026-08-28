namespace AssetBlock.Domain.Core.Enums;

public enum OutboxReplayOutcome
{
    SUCCESS = 0,
    NOT_FOUND = 1,
    NOT_DEAD_LETTERED = 2
}
