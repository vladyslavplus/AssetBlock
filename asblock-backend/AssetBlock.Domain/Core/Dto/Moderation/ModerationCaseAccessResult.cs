namespace AssetBlock.Domain.Core.Dto.Moderation;

public enum ModerationCaseAccessStatus
{
    FOUND,
    NOT_FOUND,
    SELF_OWNED_DENIED
}

public sealed record ModerationCaseAccessResult(
    ModerationCaseAccessStatus Status,
    ModerationCaseSummary? Summary);
