using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record SellerAssetListItem(
    Guid Id,
    string Title,
    string? Description,
    decimal Price,
    Guid CategoryId,
    string? CategoryName,
    Guid AuthorId,
    string AuthorUsername,
    DateTimeOffset CreatedAt,
    IReadOnlyList<string> Tags,
    double AverageRating,
    Guid LatestVersionId,
    int LatestVersionNumber,
    Guid? CurrentReadyVersionId,
    AssetVersionProcessingStatus LatestProcessingStatus,
    DateTimeOffset LatestProcessingUpdatedAt,
    string? LatestProcessingErrorCode,
    string? LatestProcessingErrorSummary);
