using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record SellerAssetDetailItem(
    Guid Id,
    string Title,
    string? Description,
    decimal Price,
    Guid CategoryId,
    string? CategoryName,
    Guid AuthorId,
    string AuthorUsername,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    IReadOnlyList<string> Tags,
    Guid LatestVersionId,
    int LatestVersionNumber,
    Guid? CurrentReadyVersionId,
    AssetVersionProcessingStatus LatestProcessingStatus,
    DateTimeOffset LatestProcessingUpdatedAt,
    string? LatestProcessingErrorCode,
    string? LatestProcessingErrorSummary);
