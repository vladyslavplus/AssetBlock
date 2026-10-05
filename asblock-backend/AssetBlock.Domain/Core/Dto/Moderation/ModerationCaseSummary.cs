using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Moderation;

public sealed record ModerationCaseSummary(
    Guid SubmissionId,
    Guid AssetId,
    Guid AssetVersionId,
    ModerationSubmissionState State,
    long CaseRevision,
    string ContentSha256,
    string PolicyVersion);
