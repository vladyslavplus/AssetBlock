namespace AssetBlock.Domain.Core.Enums;

/// <summary>Per-submission moderation lifecycle. WITHDRAWN uses a separate reason field.</summary>
public enum ModerationSubmissionState
{
    DRAFT,
    SUBMITTED,
    IN_REVIEW,
    CHANGES_REQUESTED,
    APPROVED,
    REJECTED,
    WITHDRAWN
}
