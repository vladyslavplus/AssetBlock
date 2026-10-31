namespace AssetBlock.Domain.Core.Dto.Moderation;

public enum ModerationCaseAccessStatus
{
    FOUND,
    NOT_FOUND,
    DENIED_ROLE,
    SELF_OWNED_DENIED
}

public sealed record ModerationCaseAccessResult(
    ModerationCaseAccessStatus Status,
    ModerationCaseSummary? Summary);
