namespace AssetBlock.Domain.Core.Dto.Assets;

public sealed record AssetListItem(
    Guid Id,
    string Title,
    string? Description,
    decimal Price,
    Guid CategoryId,
    string? CategoryName,
    Guid AuthorId,
    DateTimeOffset CreatedAt);
