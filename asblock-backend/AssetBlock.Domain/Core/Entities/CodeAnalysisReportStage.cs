using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>Immutable per-stage outcome admitted for one finalized report.</summary>
public class CodeAnalysisReportStage : BaseEntity
{
    public required Guid ReportId { get; set; }

    public required string Stage { get; set; }

    public required CodeAnalysisStageApplicability Applicability { get; set; }

    public required CodeAnalysisStageOutcome Outcome { get; set; }

    public string? ErrorCode { get; set; }

    public required bool Retryable { get; set; }

    public required int DurationMilliseconds { get; set; }

    public string? InputSha256 { get; set; }

    public string? OutputSha256 { get; set; }

    /// <summary>Observed tool, runtime, and signature identity for this stage.</summary>
    public required string ToolIdentityJson { get; set; }

    public required string CountsJson { get; set; }

    /// <summary>Parent run and stage whose verified artifact was reused instead of recomputed.</summary>
    public Guid? ReuseParentRunId { get; set; }

    public Guid? ReuseParentStageId { get; set; }
}
