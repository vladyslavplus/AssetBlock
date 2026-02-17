using AssetBlock.Domain.Core.Dto.Paging;

namespace AssetBlock.Domain.Core.Dto.Categories;

/// <summary>
/// Request for listing categories with paging and sorting.
/// </summary>
public sealed class GetCategoriesRequest : PagedRequest
{
    /// <summary>Optional search by name or slug.</summary>
    public string? Search { get; set; }

    /// <summary>Allowed sort fields: Name, Slug, ID. Default: Name.</summary>
    public static readonly IReadOnlySet<string> AllowedSortBy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Name", "Slug", "Id"
    };
}
