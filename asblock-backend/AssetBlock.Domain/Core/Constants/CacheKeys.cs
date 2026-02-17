using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Categories;

namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Centralized cache key builders for Redis/distributed cache.
/// </summary>
public static class CacheKeys
{
    private const string PREFIX = "assetblock";

    /// <summary>Prefix for all assets list cache keys. Use with RemoveByPrefix to invalidate list cache.</summary>
    public const string ASSETS_LIST_PREFIX = PREFIX + ":assets:list";

    /// <summary>Prefix for all categories list cache keys.</summary>
    public const string CATEGORIES_LIST_PREFIX = PREFIX + ":categories:list";

    public static string AssetsList(GetAssetsRequest request)
    {
        var search = NormalizeSearch(request.Search);
        var categoryId = request.CategoryId.HasValue ? request.CategoryId.Value.ToString() : "none";
        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) ? "none" : request.SortBy.Trim();
        return $"{ASSETS_LIST_PREFIX}:{request.Page}:{request.PageSize}:{search}:{categoryId}:{sortBy}:{request.SortDirection}";
    }

    public static string CategoriesList(GetCategoriesRequest request)
    {
        var search = NormalizeSearch(request.Search);
        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) ? "none" : request.SortBy.Trim();
        return $"{CATEGORIES_LIST_PREFIX}:{request.Page}:{request.PageSize}:{search}:{sortBy}:{request.SortDirection}";
    }

    private static string NormalizeSearch(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(":", "_", StringComparison.Ordinal).ToLowerInvariant();
    }
}
