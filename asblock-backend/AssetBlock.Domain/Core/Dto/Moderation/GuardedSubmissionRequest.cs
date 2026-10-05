namespace AssetBlock.Domain.Core.Dto.Moderation;

public sealed record GuardedSubmissionRequest(
    Guid OwnerUserId,
    Guid AssetId,
    Guid AssetVersionId,
    Guid WorkspaceId,
    long ExpectedWorkspaceRevision,
    long ExpectedCaseRevision,
    Guid OperationId,
    string RequestDigest,
    Guid? CodeAnalysisReportHeaderId);
