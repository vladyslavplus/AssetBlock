using AssetBlock.Domain.Core.Primitives.BaseEntities;
namespace AssetBlock.Domain.Core.Entities;
/// <summary>Frozen proposed tuple; only assessment completion advances its workflow revision.</summary>
public sealed class CoverageReviewRequest : BaseEntity
{
    public required Guid AssetId { get; init; }
    public required Guid AssetVersionId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required Guid ReportId { get; init; }
    public required Guid OwnerUserId { get; init; }
    public required string ContentSha256 { get; init; }
    public required int DeclarationRevision { get; init; }
    public required int MaterialRevision { get; init; }
    public required int SellerEvidenceRevision { get; init; }
    public required string PolicyVersion { get; init; }
    public required string PolicyDigest { get; init; }
    public required string TupleDigest { get; init; }
    public long RequestedWorkspaceRevision { get; init; }
    public long Revision { get; set; } = 1;
    public bool IsOpen { get; set; } = true;
}
