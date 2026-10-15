namespace AssetBlock.Domain.Core.Dto;

public abstract record AssetProcessingPayload;
public abstract record AssetProcessingResult;

public sealed record ArchiveInspectionPayload : AssetProcessingPayload;
public sealed record ArchiveInspectionResult(int FileCount, long TotalSizeUncompressed) : AssetProcessingResult;

public sealed record MalwareScanPayload(string PolicyVersion) : AssetProcessingPayload;
public sealed record MalwareScanResult(bool IsClean) : AssetProcessingResult;

public sealed record ListingCopilotPayload(string PolicyVersion) : AssetProcessingPayload;
public sealed record ListingCopilotResult(bool Success, string ContentHash) : AssetProcessingResult;

public sealed record EmbeddingGenerationPayload(
    Guid AssetId,
    Guid AssetVersionId,
    long TargetRevision,
    string ContentHash,
    string ModelKey,
    string ContentSchemaVersion) : AssetProcessingPayload;

public sealed record EmbeddingGenerationResult(
    Guid AssetId,
    long SourceRevision,
    string ContentHash,
    string ModelKey,
    int Dimension,
    bool Success) : AssetProcessingResult;

/// <summary>
/// Compact run-scoped references for a code analysis job. Deliberately carries no report body:
/// the reservation binding is persisted on the job and run columns at claim time, and the admitted
/// report is stored in bounded dedicated tables rather than in job JSON.
/// <para>
/// <paramref name="ReuseSourceRunId"/> and <paramref name="ReuseSourceInputDigest"/> name the run whose
/// verified extraction, token and vector work this run may reuse. It is only ever set when the
/// declaration alone changed and every other analyzed identity — content, material metadata, policy,
/// security proof and reference binding — is unchanged, so the encoder never has to run again.
/// </para>
/// </summary>
public sealed record CodeAnalysisPayload(
    Guid AnalysisRunId,
    string InputDigest,
    string PolicyDigest,
    string ContentSha256,
    Guid ArchiveEvidenceId,
    Guid MalwareEvidenceId,
    int SecurityGeneration,
    Guid? ReuseSourceRunId = null,
    string? ReuseSourceInputDigest = null,
    string? ReuseSourceMaterialDigest = null) : AssetProcessingPayload;

/// <summary>Compact admitted-report summary. Never the full report.</summary>
public sealed record CodeAnalysisResult(
    Guid ReportId,
    string PythonReportId,
    string ReportDigest,
    Guid AnalysisRunId,
    string CoverageLevel,
    int AnalyzedFileCount,
    int OmissionCount,
    int FindingCount,
    string Disposition) : AssetProcessingResult;
