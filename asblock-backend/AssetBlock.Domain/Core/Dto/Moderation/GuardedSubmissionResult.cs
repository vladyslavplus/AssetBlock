namespace AssetBlock.Domain.Core.Dto.Moderation;

public enum GuardedSubmissionStatus
{
    BLOCKED,
    CONFLICT,
    REPLAYED,
    CREATED
}

public sealed record GuardedSubmissionResult(
    GuardedSubmissionStatus Status,
    Guid? SubmissionId,
    string? BlockedReasonCode);
