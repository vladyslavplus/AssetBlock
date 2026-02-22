using AssetBlock.Domain.Core.Dto.Paging;

namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>
/// Request for listing assets with paging, sort, and filters.
/// </summary>
public sealed record GetAssetsRequest : PagedRequest
{
    public string? Search { get; init; }
    public Guid? CategoryId { get; init; }
    public IReadOnlyList<string>? Tags { get; init; }
    public decimal? MinPrice { get; init; }
    public decimal? MaxPrice { get; init; }

    public static readonly IReadOnlySet<string> AllowedSortBy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Title", "Price", "CreatedAt", "Id"
    };
}
