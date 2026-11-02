using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;
namespace AssetBlock.Domain.Core.Entities;
/// <summary>Immutable private answer. Current evidence snapshots reference its identity; it never resolves a finding.</summary>
public sealed class SellerResponse : BaseEntity
{
    public required Guid AssetId { get; init; }
    public required Guid AssetVersionId { get; init; }
    public required Guid WorkspaceId { get; init; }
    public required Guid WorkspaceVersionScopeKey { get; init; }
    public required Guid ReportId { get; init; }
    public required Guid FindingId { get; init; }
    public required int SellerEvidenceRevision { get; init; }
    public required SellerResponseType ResponseType { get; init; }
    public required string Text { get; init; }
    public required Guid ActorUserId { get; init; }
}
