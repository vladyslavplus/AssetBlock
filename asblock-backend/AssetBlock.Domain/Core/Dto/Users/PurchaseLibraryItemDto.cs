using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Users;

public sealed record PurchaseLibraryItemDto(
    Guid Id,
    Guid OrderId,
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
    string Currency,
    PurchaseSource Source,
    Guid? BundleId,
    string? BundleTitle);
