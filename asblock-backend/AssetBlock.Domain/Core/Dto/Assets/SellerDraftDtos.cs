using System.Text.Json.Serialization;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>Working metadata payload of a draft save. Price is intentionally absent: it uses the dedicated price operation.</summary>
public sealed record SellerDraftMaterialPayload(
    string Title,
    string? Description,
    Guid CategoryId,
    IReadOnlyList<string> Tags);

/// <summary>A third-party or own-work source component inside a declaration.</summary>
public sealed record SourceDeclarationComponent(
    string ComponentId,
    string Name,
    string? PackagePathOrRange,
    string SourceUrl,
    string? KnownVersion,
    string License,
    IReadOnlyList<string> NoticeLocations,
    string? Modifications,
    IReadOnlyList<string> PermissionEvidenceReferences,
    SourceComponentOrigin Origin);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SourceComponentOrigin
{
    OWN_CONTRIBUTION,
    THIRD_PARTY
}

/// <summary>Structured pre-upload/version source declaration. URLs are stored data only; nothing is fetched.</summary>
public sealed record SourceDeclarationPayload(
    string OwnContributionSummary,
    string? OwnChanges,
    string? EarlierWork,
    bool RedistributionAcknowledged,
    string DisclosurePolicyVersion,
    IReadOnlyList<SourceDeclarationComponent> Components);

/// <summary>Result of a draft revision save (material metadata or declaration).</summary>
public sealed record DraftSaveResult(
    Guid WorkspaceId,
    long WorkspaceRevision,
    int HeadRevision,
    bool Replayed);

/// <summary>Aggregate draft view returned to the owner. Contains working metadata and declaration completeness only.</summary>
public sealed record SellerDraftSnapshotDto(
    Guid AssetId,
    Guid WorkspaceId,
    long WorkspaceRevision,
    long CaseRevision,
    SellerDraftMaterialPayload Material,
    SourceDeclarationPayload? Declaration,
    bool DeclarationComplete,
    Guid? LatestVersionId,
    int? LatestVersionNumber);

public sealed record SellerDraftCreatedDto(
    Guid AssetId,
    Guid WorkspaceId,
    long WorkspaceRevision,
    bool Replayed = false);

/// <summary>
/// Typed declaration read result for the owning workspace (pre-upload or version-scoped).
/// WorkspaceId/WorkspaceRevision feed the next save's CAS; Declaration is null when the
/// workspace exists but no declaration revision has been stored yet.
/// </summary>
public sealed record SellerDeclarationSnapshotDto(
    Guid AssetId,
    Guid? AssetVersionId,
    Guid WorkspaceId,
    long WorkspaceRevision,
    int HeadRevision,
    SourceDeclarationPayload? Declaration,
    bool DeclarationComplete);

/// <summary>Owner-safe version review status. No private evidence, storage locators, or scanner output.</summary>
public sealed record SellerVersionReviewDto(
    Guid AssetId,
    Guid AssetVersionId,
    int VersionNumber,
    AssetVersionProcessingStatus ProcessingStatus,
    string? ProcessingErrorCode,
    string? ProcessingErrorSummary,
    SellerAnalysisAvailability Analysis,
    ModerationSubmissionState ModerationState,
    bool PublicationEligible,
    bool CanUpload,
    bool CanSubmit,
    IReadOnlyList<string> BlockedReasons);

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum SellerAnalysisAvailability
{
    ANALYSIS_NOT_AVAILABLE,
    ANALYSIS_AVAILABLE
}

public sealed record SellerDraftCreateRequest(
    Guid OperationId,
    string Title,
    string? Description,
    decimal Price,
    Guid CategoryId,
    int? DownloadLimitPerHour);

public sealed record SellerDraftSaveRequest(
    Guid OperationId,
    long ExpectedWorkspaceRevision,
    SellerDraftMaterialPayload Material);

public sealed record SellerDeclarationSaveRequest(
    Guid OperationId,
    long ExpectedWorkspaceRevision,
    SourceDeclarationPayload Declaration);

public sealed record AssetPriceUpdateRequest(decimal Price);

public sealed record AssetVersionSubmissionRequest(
    Guid WorkspaceId,
    long ExpectedWorkspaceRevision,
    long ExpectedCaseRevision,
    Guid OperationId);

public sealed record SubmissionWithdrawRequest(long ExpectedCaseRevision, Guid OperationId);
