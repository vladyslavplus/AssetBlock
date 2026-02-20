using AssetBlock.Domain.Core.Dto.Paging;

namespace AssetBlock.Domain.Core.Dto.Reviews;

public sealed record GetReviewsRequest : PagedRequest
{
    public string? Search { get; set; }

    public static readonly IReadOnlySet<string> AllowedSortBy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "CreatedAt", "Rating"
    };
}
