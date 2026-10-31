using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;
namespace AssetBlock.Domain.Core.Entities;
/// <summary>Append-only finding disposition within one case-revision decision attempt.</summary>
public sealed class ModerationFindingDisposition : BaseEntity
{
    public required Guid AssetId { get; init; }
    public required Guid AssetVersionId { get; init; }
    public required Guid SubmissionId { get; init; }
    public required Guid ReportId { get; init; }
    public required Guid FindingId { get; init; }
    public required Guid ModeratorUserId { get; init; }
    public required long CaseRevision { get; init; }
    public required FindingDispositionType Disposition { get; init; }
    public required string Rationale { get; init; }
}
