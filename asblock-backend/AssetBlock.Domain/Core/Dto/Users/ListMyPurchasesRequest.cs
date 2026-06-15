using AssetBlock.Domain.Core.Dto.Paging;

namespace AssetBlock.Domain.Core.Dto.Users;

/// <summary>
/// Paging for GET users/me/purchases. Default sort is newest purchase first.
/// </summary>
public sealed record ListMyPurchasesRequest : PagedRequest
{
    public override SortDirection SortDirection { get; init; } = SortDirection.DESC;

    public static readonly IReadOnlySet<string> AllowedSortBy = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "PurchasedAt"
    };
}
