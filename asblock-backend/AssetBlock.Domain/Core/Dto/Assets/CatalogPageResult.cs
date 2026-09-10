namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>
/// Endpoint-specific catalog page result including truncation flag for bounded relevance windows.
/// Preserves public pagination contracts (Items, TotalCount, Page, PageSize, TotalPages) while adding <see cref="IsTruncated"/>.
/// </summary>
public sealed record CatalogPageResult<T>(
    IReadOnlyList<T> Items,
    int TotalCount,
    int Page,
    int PageSize,
    bool IsTruncated = false)
{
    public int TotalPages => PageSize > 0 ? (int)Math.Ceiling(TotalCount / (double)PageSize) : 0;
}
