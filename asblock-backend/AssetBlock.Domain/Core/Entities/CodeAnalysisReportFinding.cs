using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.BaseEntities;

namespace AssetBlock.Domain.Core.Entities;

/// <summary>
/// Seller-safe concern recorded for one report. Stores locations, opaque reference identity and
/// scores only: never raw source, foreign paths, snippets, or private corpus identity.
/// </summary>
public class CodeAnalysisReportFinding : BaseEntity
{
    public required Guid ReportId { get; set; }

    /// <summary>Stable finding key inside the report.</summary>
    public required string FindingKey { get; set; }

    public required string FindingType { get; set; }

    /// <summary>Canonical relative path of the seller's own file.</summary>
    public required string OwnScopePath { get; set; }

    /// <summary>Bounded excerpt from the seller's own source, never from a compared source.</summary>
    public required string OwnExcerpt { get; set; }

    public int? OwnStartByte { get; set; }

    public int? OwnEndByte { get; set; }

    /// <summary>One-based source line reported by the analysis service, when available.</summary>
    public int? OwnStartLine { get; set; }

    /// <summary>One-based inclusive end line reported by the analysis service, when available.</summary>
    public int? OwnEndLine { get; set; }

    /// <summary>Opaque reference identity; carries no seller-visible source identity when private.</summary>
    public string? ForeignSourceId { get; set; }

    /// <summary>Distinguishes the seller's own earlier work from a foreign comparison.</summary>
    public string? ForeignOrigin { get; set; }

    public required CodeAnalysisFindingSensitivity ForeignSensitivity { get; set; }

    /// <summary>Public attribution payload; null whenever attribution is not permitted.</summary>
    public string? PublicAttributionJson { get; set; }

    public double? TokenScore { get; set; }

    public double? SemanticScore { get; set; }

    public int? TokenRank { get; set; }

    public int? SemanticRank { get; set; }

    public required string ReasonText { get; set; }

    public required string RequestedAction { get; set; }
}
