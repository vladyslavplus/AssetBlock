using System.Globalization;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.Domain.Core.Dto.Reviews;
using AssetBlock.Domain.Core.Dto.Tags;

namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Centralized cache key builders for Redis/distributed cache.
/// </summary>
public static class CacheKeys
{
    private const string PREFIX = "assetblock";
    private const string INVALIDATION_INDEX_PREFIX = PREFIX + ":cache:index";
    private const string DOWNLOAD_COUNTER_PREFIX = PREFIX + ":downloads:hourly";

    public const string SOCIAL_PLATFORMS = PREFIX + ":social_platforms:list";

    /// <summary>Prefix for all assets list cache keys. Use with RemoveByPrefix to invalidate list cache.</summary>
    public const string ASSETS_LIST_PREFIX = PREFIX + ":assets:list";

    /// <summary>Prefix for all categories list cache keys.</summary>
    public const string CATEGORIES_LIST_PREFIX = PREFIX + ":categories:list";

    /// <summary>Prefix for all tags list cache keys.</summary>
    public const string TAGS_LIST_PREFIX = PREFIX + ":tags:list";

    /// <summary>Used with RemoveByPrefix to invalidate cached review lists.</summary>
    public const string REVIEWS_LIST_PREFIX = PREFIX + ":reviews:list";

    /// <summary>Used to cache single review entries for targeted invalidation.</summary>
    private const string REVIEW_ITEM_PREFIX = PREFIX + ":reviews:item";

    public static string ReviewsListAssetPrefix(Guid assetId) => $"{REVIEWS_LIST_PREFIX}:{assetId}";

    public static string AssetsList(GetAssetsRequest request)
    {
        var authorId = request.AuthorId.HasValue ? request.AuthorId.Value.ToString() : "none";
        var search = NormalizeSearch(request.Search);
        var categoryId = request.CategoryId.HasValue ? request.CategoryId.Value.ToString() : "none";
        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) ? "none" : request.SortBy.Trim();
        var minPrice = request.MinPrice.HasValue ? request.MinPrice.Value.ToString("F2", CultureInfo.InvariantCulture) : "none";
        var maxPrice = request.MaxPrice.HasValue ? request.MaxPrice.Value.ToString("F2", CultureInfo.InvariantCulture) : "none";
        var tags = request.Tags is { Count: > 0 }
            ? string.Join(",",
                request.Tags
                    .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                    .Select(t => t.Trim().ToLowerInvariant())
                    .Where(t => t.Length > 0)
                    .Distinct()
                    .OrderBy(t => t))
            : "none";
        return $"{ASSETS_LIST_PREFIX}:{request.Page}:{request.PageSize}:{authorId}:{search}:{categoryId}:{minPrice}:{maxPrice}:{tags}:{sortBy}:{request.SortDirection}";
    }

    public static string CategoriesList(GetCategoriesRequest request)
    {
        var search = NormalizeSearch(request.Search);
        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) ? "none" : request.SortBy.Trim();
        return $"{CATEGORIES_LIST_PREFIX}:{request.Page}:{request.PageSize}:{search}:{sortBy}:{request.SortDirection}";
    }

    public static string TagsList(GetTagsRequest request)
    {
        var search = NormalizeSearch(request.Search);
        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) ? "none" : request.SortBy.Trim();
        return $"{TAGS_LIST_PREFIX}:{request.Page}:{request.PageSize}:{search}:{sortBy}:{request.SortDirection}";
    }

    public static string ReviewsList(Guid assetId, GetReviewsRequest request)
    {
        var search = NormalizeSearch(request.Search);
        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) ? "none" : request.SortBy.Trim();
        return $"{ReviewsListAssetPrefix(assetId)}:{request.Page}:{request.PageSize}:{search}:{sortBy}:{request.SortDirection}";
    }

    public static string ReviewItem(Guid reviewId) => $"{REVIEW_ITEM_PREFIX}:{reviewId}";

    /// <summary>Builds the per-user, per-asset counter key for one UTC hourly download window.</summary>
    public static string DownloadCounter(Guid assetId, Guid userId, DateTimeOffset now)
    {
        var window = now.ToUniversalTime().ToString("yyyyMMddHH", CultureInfo.InvariantCulture);
        return $"{DOWNLOAD_COUNTER_PREFIX}:{assetId}:{userId}:{window}";
    }

    /// <summary>Returns the remaining TTL in the current UTC hourly download window.</summary>
    public static TimeSpan DownloadCounterExpiry(DateTimeOffset now)
    {
        DateTimeOffset utc = now.ToUniversalTime();
        DateTimeOffset nextHour = new DateTimeOffset(
            utc.Year,
            utc.Month,
            utc.Day,
            utc.Hour,
            0,
            0,
            TimeSpan.Zero).AddHours(1);
        return nextHour - utc;
    }

    /// <summary>Returns exact invalidation prefixes tracked for a cache key.</summary>
    public static IReadOnlyList<string> InvalidationPrefixes(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        if (key.StartsWith(ASSETS_LIST_PREFIX + ":", StringComparison.Ordinal))
        {
            return [ASSETS_LIST_PREFIX];
        }

        if (key.StartsWith(CATEGORIES_LIST_PREFIX + ":", StringComparison.Ordinal))
        {
            return [CATEGORIES_LIST_PREFIX];
        }

        if (key.StartsWith(TAGS_LIST_PREFIX + ":", StringComparison.Ordinal))
        {
            return [TAGS_LIST_PREFIX];
        }

        if (key.StartsWith(REVIEWS_LIST_PREFIX + ":", StringComparison.Ordinal))
        {
            ReadOnlySpan<char> suffix = key.AsSpan(REVIEWS_LIST_PREFIX.Length + 1);
            var separator = suffix.IndexOf(':');
            if (separator > 0 && Guid.TryParse(suffix[..separator], out _))
            {
                return [$"{REVIEWS_LIST_PREFIX}:{suffix[..separator]}"];
            }
        }

        if (key.StartsWith(REVIEW_ITEM_PREFIX + ":", StringComparison.Ordinal))
        {
            return [key];
        }

        return [];
    }

    /// <summary>Redis set containing keys owned by one exact invalidation prefix.</summary>
    public static string InvalidationIndex(string prefix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        return $"{INVALIDATION_INDEX_PREFIX}:{prefix}";
    }

    private const string ANALYTICS_PREFIX = PREFIX + ":analytics:seller";

    public static string SellerAnalyticsOverview(Guid sellerId, DateOnly from, DateOnly to) =>
        $"{ANALYTICS_PREFIX}:{sellerId}:overview:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";

    public static string SellerAnalyticsProducts(Guid sellerId, AnalyticsProductsRequest request) =>
        $"{ANALYTICS_PREFIX}:{sellerId}:products:{request.From:yyyy-MM-dd}:{request.To:yyyy-MM-dd}" +
        $":{request.ProductType}:{request.Sort}:{request.Direction}:{request.Page}:{request.PageSize}";

    public static string SellerAnalyticsSales(Guid sellerId, AnalyticsSalesRequest request) =>
        $"{ANALYTICS_PREFIX}:{sellerId}:sales:{request.From:yyyy-MM-dd}:{request.To:yyyy-MM-dd}" +
        $":{request.ProductType}:{request.PageSize}:{request.Cursor ?? "start"}";

    public static string SellerAnalyticsAssetDetail(
        Guid sellerId,
        Guid assetId,
        DateOnly from,
        DateOnly to) =>
        $"{ANALYTICS_PREFIX}:{sellerId}:asset:{assetId}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";

    public static string SellerAnalyticsBundleDetail(
        Guid sellerId,
        Guid bundleId,
        DateOnly from,
        DateOnly to) =>
        $"{ANALYTICS_PREFIX}:{sellerId}:bundle:{bundleId}:{from:yyyy-MM-dd}:{to:yyyy-MM-dd}";

    public static string SellerAnalyticsCollections(Guid sellerId, AnalyticsCollectionsRequest request) =>
        $"{ANALYTICS_PREFIX}:{sellerId}:collections:{request.From:yyyy-MM-dd}:{request.To:yyyy-MM-dd}" +
        $":{request.Sort}:{request.Direction}:{request.Page}:{request.PageSize}";

    private static string NormalizeSearch(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? "none" : value.Trim().Replace(":", "_", StringComparison.Ordinal).ToLowerInvariant();
    }
}
