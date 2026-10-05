namespace AssetBlock.Domain.Core.Dto.Moderation;

public sealed record AssetDraftWorkspaceSnapshot(
    Guid WorkspaceId,
    Guid AssetId,
    Guid? AssetVersionId,
    Guid WorkspaceVersionScopeKey,
    long WorkspaceRevision,
    long CaseRevision,
    int MaterialMetadataHeadRevision,
    int SourceDeclarationHeadRevision,
    int SellerEvidenceHeadRevision);
