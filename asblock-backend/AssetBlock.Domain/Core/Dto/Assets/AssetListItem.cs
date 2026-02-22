namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record AssetListItem(
    Guid Id,
    string Title,
    string? Description,
    decimal Price,
    Guid CategoryId,
    string? CategoryName,
    Guid AuthorId,
    string AuthorUsername,
    DateTimeOffset CreatedAt,
    List<string> Tags,
    double AverageRating);
