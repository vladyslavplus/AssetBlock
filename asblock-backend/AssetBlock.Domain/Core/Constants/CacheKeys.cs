using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.Domain.Core.Dto.Reviews;

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

    /// <summary>Used with RemoveByPrefix to invalidate cached review lists.</summary>
    public const string REVIEWS_LIST_PREFIX = PREFIX + ":reviews:list";

    /// <summary>Used to cache single review entries for targeted invalidation.</summary>
    private const string REVIEW_ITEM_PREFIX = PREFIX + ":reviews:item";

    public static string ReviewsListAssetPrefix(Guid assetId) => $"{REVIEWS_LIST_PREFIX}:{assetId}";

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

    public static string ReviewsList(Guid assetId, GetReviewsRequest request)
    {
        var search = NormalizeSearch(request.Search);
        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) ? "none" : request.SortBy.Trim();
        return $"{ReviewsListAssetPrefix(assetId)}:{request.Page}:{request.PageSize}:{search}:{sortBy}:{request.SortDirection}";
    }

    public static string ReviewItem(Guid reviewId) => $"{REVIEW_ITEM_PREFIX}:{reviewId}";

    private static string NormalizeSearch(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(":", "_", StringComparison.Ordinal).ToLowerInvariant();
    }
}
