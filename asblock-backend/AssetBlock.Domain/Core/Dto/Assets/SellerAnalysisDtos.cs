using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record SellerAnalysisRetryExpectation(
    Guid RunId,
    long WorkspaceRevision,
    string PolicyDigest,
    string InputDigest);

public sealed record SellerAnalysisStageDto(
    string Stage,
    string Applicability,
    string Outcome,
    bool Retryable);

/// <summary>Owner-safe projection from the selected immutable run and admitted report.</summary>
public sealed record SellerAnalysisSummaryDto(
    Guid AssetId,
    Guid AssetVersionId,
    SellerAnalysisLifecycle Lifecycle,
    Guid? RunId,
    Guid? ReportId,
    bool ReportIsCurrent,
    CodeAnalysisCoverageLevel? CoverageLevel,
    int AnalyzedFileCount,
    int OmissionCount,
    int FindingCount,
    bool FindingDetailsAvailable,
    DateTimeOffset? CreatedAt,
    DateTimeOffset? CompletedAt,
    string? ErrorCode,
    string? ErrorMessage,
    string? ActionHint,
    bool CanRetry,
    SellerAnalysisRetryExpectation? RetryExpectation,
    bool CanSubmit,
    bool ReviewEligible,
    string ComparisonScope,
    IReadOnlyList<string> Limitations,
    IReadOnlyList<SellerAnalysisStageDto> Stages,
    string? SubmissionBlockedReason = null);

public sealed record SellerPublicAttributionDto(
    string Label,
    string? Url);

public sealed record SellerAnalysisFindingDto(
    Guid Id,
    string FindingType,
    string OwnPath,
    string OwnExcerpt,
    int? StartByte,
    int? EndByte,
    int? StartLine,
    int? EndLine,
    string Reason,
    string RequestedAction,
    string? ReferenceKind,
    SellerPublicAttributionDto? PublicAttribution);

public sealed record SellerAnalysisFindingsPageDto(
    Guid AssetId,
    Guid AssetVersionId,
    Guid ReportId,
    bool IsCurrent,
    CodeAnalysisCoverageLevel CoverageLevel,
    IReadOnlyList<SellerAnalysisFindingDto> Items,
    int TotalCount,
    bool DetailsAvailable,
    int Page,
    int PageSize,
    int TotalPages);

public sealed record SellerAnalysisRetryRequest(
    Guid OperationId,
    Guid ExpectedRunId,
    long ExpectedWorkspaceRevision,
    string ExpectedPolicyDigest,
    string ExpectedInputDigest);

public sealed record SellerAnalysisRetryAcceptedDto(
    Guid OperationId,
    Guid AssetId,
    Guid AssetVersionId,
    SellerAnalysisRetryStatus Status,
    Guid? RunId = null,
    Guid? JobId = null,
    Guid? SecurityRecheckId = null,
    bool Replayed = false);

public enum SellerAnalysisRetryStatus
{
    ACCEPTED,
    RECHECK_QUEUED,
    CONFLICT,
    NOT_FOUND
}

public sealed record SellerAnalysisRetryStoreResult(
    SellerAnalysisRetryStatus Status,
    SellerAnalysisRetryAcceptedDto? Accepted = null,
    Guid? SecurityRecheckId = null);
