using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>Minimum trusted analysis identity boundary for a version and content hash.</summary>
public class CodeAnalysisReportHeader : BaseEntity
{
    public required Guid AssetId { get; init; }
    public required Guid AssetVersionId { get; init; }
    public required string ContentSha256 { get; init; }
    public required string PolicyVersion { get; init; }
    public required int InputRevision { get; init; }
    public required CodeAnalysisReportPurpose Purpose { get; init; }
    public required bool CanAuthorizePublication { get; init; }
    public required bool IsFinalized { get; init; }
    public DateTimeOffset? FinalizedAt { get; init; }
    public required int ReportSchemaVersion { get; init; }

    public Asset Asset { get; set; } = null!;
    public AssetVersion AssetVersion { get; set; } = null!;
}
