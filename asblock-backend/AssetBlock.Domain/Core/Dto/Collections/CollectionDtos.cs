using AssetBlock.Domain.Core.Dto.Paging;

namespace AssetBlock.Domain.Core.Dto.Collections;

public sealed record ListCollectionsRequest : PagedRequest
{
    public string? Search { get; init; }

    public override SortDirection SortDirection { get; init; } = SortDirection.DESC;

    public static readonly IReadOnlySet<string> AllowedSortBy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PublishedAt",
        "CreatedAt",
        "Title"
    };
}

public sealed record ListMyCollectionsRequest : PagedRequest
{
    public string? Search { get; init; }
    public string? Status { get; init; }

    public override SortDirection SortDirection { get; init; } = SortDirection.DESC;

    public static readonly IReadOnlySet<string> AllowedSortBy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "UpdatedAt",
        "CreatedAt",
        "Title",
        "Status"
    };
}

public sealed record CreateCollectionRequest(string Title, string? Description);

public sealed record UpdateCollectionRequest(string Title, string? Description);

public sealed record AddCollectionItemRequest(Guid AssetId);

public sealed record ReorderCollectionItemsRequest(IReadOnlyList<Guid> AssetIds);

public sealed record CreateCollectionResponse(Guid Id);

public sealed record CollectionListItemDto(
    Guid Id,
    string Title,
    string? Description,
    string Status,
    DateTimeOffset? PublishedAt,
    DateTimeOffset CreatedAt,
    Guid SellerId,
    string SellerUsername,
    int ItemCount,
    Guid? CoverAssetId,
    string? CoverAssetTitle);

public sealed record CollectionItemDto(
    Guid AssetId,
    string Title,
    decimal Price,
    int Position,
    bool IsAvailable,
    string? UnavailableReason);

public sealed record CollectionDetailDto(
    Guid Id,
    string Title,
    string? Description,
    string Status,
    DateTimeOffset? PublishedAt,
    DateTimeOffset? ArchivedAt,
    DateTimeOffset CreatedAt,
    DateTimeOffset? UpdatedAt,
    Guid SellerId,
    string SellerUsername,
    IReadOnlyList<CollectionItemDto> Items);
