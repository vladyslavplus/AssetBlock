namespace AssetBlock.Domain.Core.Dto.Users;

public sealed record PurchaseLibraryItemDto(
    Guid Id,
    Guid AssetId,
    string AssetTitle,
    decimal Price,
    DateTimeOffset PurchasedAt,
    string AuthorUsername,
    bool HasUserReviewed,
    int PurchasedVersionNumber,
    Guid PurchasedVersionId,
    int LatestEntitledVersionNumber,
    Guid LatestEntitledVersionId,
    bool HasUpdate,
    decimal PricePaid,
    string Currency);
