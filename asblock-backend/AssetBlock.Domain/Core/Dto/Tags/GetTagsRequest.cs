using AssetBlock.Domain.Core.Dto.Paging;

namespace AssetBlock.Domain.Core.Dto.Tags;

public sealed record GetTagsRequest : PagedRequest
{
    public static readonly string[] AllowedSortBy = ["id", "name"];

    public string? Search { get; init; }
}
