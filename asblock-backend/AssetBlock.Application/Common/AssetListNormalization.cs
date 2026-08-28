using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;

namespace AssetBlock.Application.Common;

/// <summary>
/// Centralized tag and description normalization for catalog and seller asset listings.
/// </summary>
public static class AssetListNormalization
{
    /// <summary>
    /// Normalizes tags: splits by comma, trims, converts to lower invariant, filters empty strings, and deduplicates.
    /// Returns null if empty.
    /// </summary>
    public static List<string>? NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }

        var list = tags
            .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();

        return list.Count > 0 ? list : null;
    }

    /// <summary>
    /// Aligns list API with DB/detail: whitespace-only or empty description becomes null.
    /// </summary>
    public static PagedResult<AssetListItem> NormalizeDescriptions(PagedResult<AssetListItem> paged)
    {
        var items = paged.Items
            .Select(i => i with { Description = string.IsNullOrWhiteSpace(i.Description) ? null : i.Description })
            .ToList();
        return new PagedResult<AssetListItem>(items, paged.TotalCount, paged.Page, paged.PageSize);
    }

    /// <summary>
    /// Aligns seller listings list API with DB/detail: whitespace-only or empty description becomes null.
    /// </summary>
    public static PagedResult<SellerAssetListItem> NormalizeDescriptions(PagedResult<SellerAssetListItem> paged)
    {
        var items = paged.Items
            .Select(i => i with { Description = string.IsNullOrWhiteSpace(i.Description) ? null : i.Description })
            .ToList();
        return new PagedResult<SellerAssetListItem>(items, paged.TotalCount, paged.Page, paged.PageSize);
    }
}
