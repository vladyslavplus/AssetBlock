using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>Strict admission-time validation of a COMPLETED analysis envelope.</summary>
public sealed record ValidatedCodeAnalysisReport(
    string PythonReportId,
    string ReportDigest,
    string CoverageLevel,
    int AnalyzedFileCount,
    int OmissionCount,
    int FindingCount,
    IReadOnlyList<ValidatedReportStage> Stages,
    IReadOnlyList<ValidatedReportFinding> Findings,
    string ComparedSnapshotId,
    string ComparedRevision,
    IReadOnlyList<ValidatedAnalysisReuseArtifact> ReuseArtifacts,
    string RetrievalSummaryJson);

public sealed record ValidatedAnalysisReuseArtifact(
    string Stage,
    string ArtifactDigest,
    string ArtifactJson,
    int PayloadBytes);

/// <summary>Persisted immutable source identity expected on a declaration-only reuse response.</summary>
public sealed record CodeAnalysisReuseSourceBinding(
    string SourceRunId,
    string SourceInputDigest,
    string SourceDeclarationDigest,
    string SourceMaterialDigest,
    IReadOnlyList<CodeAnalysisReuseStageArtifact> Artifacts);

public sealed record ValidatedReportStage(
    string Stage,
    string Applicability,
    string Outcome,
    string? ErrorCode,
    bool Retryable,
    int DurationMilliseconds,
    string? InputSha256,
    string? OutputSha256,
    string ToolIdentityJson,
    string CountsJson,
    Guid? ReuseParentRunId = null,
    Guid? ReuseParentReportId = null,
    Guid? ReuseParentStageId = null,
    string? ReuseArtifactDigest = null);

public sealed record ValidatedReportFinding(
    string FindingKey,
    string FindingType,
    string OwnScopePath,
    string OwnExcerpt,
    int? OwnStartByte,
    int? OwnEndByte,
    string? ForeignSourceId,
    string? ForeignOrigin,
    string ForeignSensitivity,
    string? PublicAttributionJson,
    double? TokenScore,
    double? SemanticScore,
    int? TokenRank,
    int? SemanticRank,
    string ReasonText,
    string RequestedAction,
    int? OwnStartLine = null,
    int? OwnEndLine = null,
    string? RetrievalEvidenceJson = null);

public interface ICodeAnalysisReportValidator
{
    ValidatedCodeAnalysisReport ValidateCompleted(
        string responseBody,
        CodeAnalysisRun run,
        CodeAnalysisPolicyVersion policy,
        CodeAnalysisReuseSourceBinding? expectedReuseSource = null);
}
