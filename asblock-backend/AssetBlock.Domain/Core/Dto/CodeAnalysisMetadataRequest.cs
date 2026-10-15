namespace AssetBlock.Domain.Core.Dto;

/// <summary>
/// Exact request metadata sent ahead of the archive stream. Every value is built from persisted
/// server state; nothing here comes from the seller and nothing is a publication authority.
/// </summary>
public sealed record CodeAnalysisMetadataRequest(
    string RunId,
    string AssetId,
    string VersionId,
    string ContentSha256,
    string InputDigest,
    string PolicyVersion,
    string DeclarationDigest,
    string MaterialDigest,
    CodeAnalysisReservation Reservation,
    CodeAnalysisSecurityEvidence SecurityEvidence,
    IReadOnlyList<CodeAnalysisDeclaredLicense> DeclaredLicenses,
    CodeAnalysisReuseRequest? Reuse = null);

/// <summary>Verified persisted stage outputs offered only for a declaration-only change.</summary>
public sealed record CodeAnalysisReuseRequest(
    string SourceRunId,
    string SourceInputDigest,
    string SourceDeclarationDigest,
    string SourceMaterialDigest,
    IReadOnlyList<CodeAnalysisReuseStageArtifact> Artifacts);

/// <summary>One content-digest checked cache row linked to its admitted source report and stage.</summary>
public sealed record CodeAnalysisReuseStageArtifact(
    string Stage,
    string SourceReportId,
    string SourceStageId,
    string ArtifactDigest,
    string ArtifactJson);

/// <summary>Capacity reservation the service must match before it accepts any work.</summary>
public sealed record CodeAnalysisReservation(
    string ServiceKey,
    int SlotNumber,
    int Generation,
    string LeaseToken,
    string HardDeadlineUtc);

/// <summary>Exact security evidence this analysis is admitted against.</summary>
public sealed record CodeAnalysisSecurityEvidence(
    string ContentSha256,
    string ArchiveEvidenceId,
    string MalwareEvidenceId,
    int SecurityGeneration);

/// <summary>One declared license component, projected from the persisted seller declaration.</summary>
public sealed record CodeAnalysisDeclaredLicense(string ScopePath, string Spdx);

/// <summary>
/// What one authenticated request actually established about the external execution. Only a
/// complete response or a confirmed pre-acceptance refusal proves the execution is gone.
/// </summary>
public sealed record CodeAnalysisServiceResponse(
    AnalysisExecutionDisposition Disposition,
    string? ResponseBody,
    string? ErrorCode,
    int StatusCode);
