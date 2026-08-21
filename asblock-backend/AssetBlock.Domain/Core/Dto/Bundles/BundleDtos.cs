using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Bundles;

public sealed record ListBundlesRequest : PagedRequest
{
    public Guid? SellerId { get; init; }
    public string? Search { get; init; }
    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }

    public override SortDirection SortDirection { get; init; } = SortDirection.DESC;

    public static readonly IReadOnlySet<string> AllowedSortBy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CreatedAt",
        "Title",
        "Price"
    };
}

public sealed record ListMyBundlesRequest : PagedRequest
{
    public string? Search { get; init; }
    public bool? ArchivedOnly { get; init; }

    public override SortDirection SortDirection { get; init; } = SortDirection.DESC;

    public static readonly IReadOnlySet<string> AllowedSortBy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "UpdatedAt",
        "CreatedAt",
        "Title",
        "Price"
    };
}

public sealed record CreateBundleRequest(
    string Title,
    string? Description,
    decimal Price,
    IReadOnlyList<Guid> AssetIds);

public sealed record ReviseBundleRequest(
    string Title,
    string? Description,
    decimal Price,
    IReadOnlyList<Guid> AssetIds);

public sealed record CreateBundleResponse(Guid Id, Guid RevisionId, int RevisionNumber);

public sealed record ReviseBundleResponse(Guid Id, Guid RevisionId, int RevisionNumber);

public sealed record BundleListItemDto(
    Guid Id,
    Guid RevisionId,
    int RevisionNumber,
    string Title,
    string? Description,
    decimal Price,
    decimal ListPriceTotal,
    decimal SavingsAmount,
    decimal SavingsPercent,
    string Currency,
    int ItemCount,
    Guid SellerId,
    string SellerUsername,
    DateTimeOffset CreatedAt,
    bool IsArchived,
    bool IsAvailable);

public sealed record BundleItemDto(
    Guid? AssetId,
    string Title,
    decimal ListPrice,
    int Position,
    bool IsAvailable,
    string? UnavailableReason,
    int? CurrentVersionNumber,
    string? LicenseCode,
    string? LicenseDisplayName);

public sealed record BundleDetailDto(
    Guid Id,
    Guid RevisionId,
    int RevisionNumber,
    string Title,
    string? Description,
    decimal Price,
    decimal ListPriceTotal,
    decimal SavingsAmount,
    decimal SavingsPercent,
    string Currency,
    Guid SellerId,
    string SellerUsername,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    DateTimeOffset? ArchivedAt,
    bool IsArchived,
    bool IsAvailable,
    IReadOnlyList<BundleItemDto> Items);

/// <summary>Current bundle revision snapshot used to build a checkout draft.</summary>
public sealed record BundleCheckoutSnapshot(
    Guid BundleId,
    Guid BundleRevisionId,
    Guid SellerId,
    string Title,
    decimal Price,
    string Currency,
    decimal ListPriceTotal,
    IReadOnlyList<BundleCheckoutItemSnapshot> Items);

public sealed record BundleCheckoutItemSnapshot(
    Guid AssetId,
    Guid AssetVersionId,
    int Position,
    string AssetTitle,
    decimal ListPrice,
    int VersionNumber,
    AssetLicenseCode LicenseCode,
    string LicenseTemplateVersion,
    string LicenseDisplayName,
    string LicenseTerms);
