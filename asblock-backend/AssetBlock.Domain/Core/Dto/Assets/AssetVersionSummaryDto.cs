using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record AssetVersionSummaryDto(
    Guid Id,
    int VersionNumber,
    bool IsCurrent,
    string FileName,
    long ContentLength,
    string ContentSha256,
    string ReleaseNotes,
    DateTimeOffset CreatedAt,
    AssetLicenseSummaryDto License,
    AssetVersionProcessingStatus ProcessingStatus = AssetVersionProcessingStatus.READY,
    string? ProcessingErrorCode = null,
    string? ProcessingErrorSummary = null,
    DateTimeOffset? ProcessingUpdatedAt = null);
