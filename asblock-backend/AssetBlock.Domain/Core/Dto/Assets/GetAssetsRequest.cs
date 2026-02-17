using AssetBlock.Domain.Core.Dto.Paging;

namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>
/// Request for listing assets with paging, sort, and filters.
/// </summary>
public sealed class GetAssetsRequest : PagedRequest
{
    public string? Search { get; set; }
    public Guid? CategoryId { get; set; }

    public static readonly IReadOnlySet<string> AllowedSortBy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Title", "Price", "CreatedAt", "Id"
    };
}
