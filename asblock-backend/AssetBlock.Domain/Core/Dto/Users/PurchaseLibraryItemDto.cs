namespace AssetBlock.Domain.Core.Dto.Users;

public sealed record PurchaseLibraryItemDto(
    Guid Id,
    Guid AssetId,
    string AssetTitle,
    decimal Price,
    DateTimeOffset PurchasedAt,
    string AuthorUsername);
