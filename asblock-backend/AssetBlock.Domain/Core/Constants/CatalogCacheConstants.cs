namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Cache TTLs for catalog, review, tag, and category listings.
/// </summary>
public static class CatalogCacheConstants
{
    public static readonly TimeSpan ASSETS_LIST_TTL = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan REVIEWS_LIST_TTL = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CATEGORIES_LIST_TTL = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TAGS_LIST_TTL = TimeSpan.FromMinutes(10);
}
