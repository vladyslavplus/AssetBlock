namespace AssetBlock.Domain.Dto.Assets;

public sealed record AssetDetailItem(
    Guid Id,
    string Title,
    string? Description,
    decimal Price,
    Guid CategoryId,
    string? CategoryName,
    Guid AuthorId,
    DateTime CreatedAt,
    DateTime UpdatedAt);
