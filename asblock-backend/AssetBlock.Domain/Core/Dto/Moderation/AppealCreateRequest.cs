namespace AssetBlock.Domain.Core.Dto.Moderation;

public sealed record AppealCreateRequest(Guid OwnerUserId, Guid AssetId, Guid AssetVersionId, Guid WorkspaceId,
    long ExpectedWorkspaceRevision, long ExpectedCaseRevision, Guid OperationId, string Rationale, string RequestDigest);
