using AssetBlock.Domain.Dto.Assets;

namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Centralized cache key builders for Redis/distributed cache.
/// </summary>
public static class CacheKeys
{
    private const string PREFIX = "assetblock";

    public static string AssetsList(GetAssetsRequest request)
    {
        return $"{PREFIX}:assets:list:{request.Page}:{request.PageSize}:{request.Search}:{request.CategoryId}:{request.SortBy}:{request.SortDirection}";
    }
}
