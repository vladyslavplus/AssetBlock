using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Durable analysis report identity for a version and content hash. A header alone is never
/// trusted: new-report authority requires a persisted server admission record.
/// </summary>
public class CodeAnalysisReportHeader : BaseEntity
{
    public required Guid AssetId { get; init; }
    public required Guid AssetVersionId { get; init; }
    public required string ContentSha256 { get; init; }
    public required string PolicyVersion { get; init; }
    public required int InputRevision { get; init; }
    public required CodeAnalysisReportPurpose Purpose { get; init; }
    public required bool ReviewEligible { get; init; }
    public required bool IsFinalized { get; init; }
    public DateTimeOffset? FinalizedAt { get; init; }
    public required int ReportSchemaVersion { get; init; }

    /// <summary>Producing run. Unbound headers are untrusted and cannot satisfy report admission.</summary>
    public Guid? AnalysisRunId { get; set; }

    /// <summary>SHA-256 report identity produced by the analysis service over the pre-REPORT body.</summary>
    public string? PythonReportId { get; set; }

    /// <summary>Server-recomputed digest over the admitted report body.</summary>
    public string? ReportDigest { get; set; }

    public int? SelectionGeneration { get; set; }

    public CodeAnalysisCoverageLevel? CoverageLevel { get; set; }

    public int? AnalyzedFileCount { get; set; }

    public int? OmissionCount { get; set; }

    public int? FindingCount { get; set; }

    public CodeAnalysisReportDisposition? Disposition { get; set; }

    /// <summary>Bounded retrieval limits, overflow, reference snapshot and revision, frozen at admission.</summary>
    public string? RetrievalSummaryJson { get; set; }

    public Asset Asset { get; set; } = null!;
    public AssetVersion AssetVersion { get; set; } = null!;
}
