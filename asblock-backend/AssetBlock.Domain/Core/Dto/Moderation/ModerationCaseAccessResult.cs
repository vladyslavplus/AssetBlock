namespace AssetBlock.Domain.Core.Dto.Moderation;

public enum ModerationCaseAccessStatus
{
    APPEAL_REVIEWER_EXCLUDED,
    FOUND,
    NOT_FOUND,
    DENIED_ROLE,
    SECURITY_RESTRICTED,
    AUDIT_UNAVAILABLE,
    SELF_OWNED_DENIED
}

public sealed record ModerationCaseAccessResult(
    ModerationCaseAccessStatus Status,
    ModerationCaseSummary? Summary);
