namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record AssetDetailItem(
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
    double AverageRating,
    int CurrentVersionNumber,
    Guid CurrentVersionId,
    DateTimeOffset CurrentVersionCreatedAt,
    string CurrentFileName,
    long CurrentContentLength,
    string CurrentContentSha256,
    AssetLicenseSummaryDto CurrentLicense);
