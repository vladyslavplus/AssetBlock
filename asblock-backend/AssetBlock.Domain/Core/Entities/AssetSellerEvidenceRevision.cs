using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

public class AssetSellerEvidenceRevision : BaseEntity
{
    public required Guid AssetId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public Guid? AssetVersionId { get; init; }
    /// <summary>Workspace version-scope key bound to the linked workspace.</summary>
    public required Guid WorkspaceVersionScopeKey { get; init; }
    public required int Revision { get; init; }
    public required Guid AuthorUserId { get; init; }
    public required string ContentDigest { get; init; }
    public required int SchemaVersion { get; init; }
    public required string PayloadJson { get; init; }
    public bool IsSubmitted { get; set; }

    public Asset Asset { get; set; } = null!;
    public AssetDraftWorkspace Workspace { get; set; } = null!;
    public AssetVersion? AssetVersion { get; set; }
}
