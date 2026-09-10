namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Cache TTLs for catalog, review, tag, and category listings.
/// </summary>
public static class CatalogCacheConstants
{
    public static readonly TimeSpan AssetsListTtl = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan HybridAssetsListTtl = TimeSpan.FromMinutes(2);
    public static readonly TimeSpan LexicalFallbackListTtl = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan QueryVectorTtl = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan ReviewsListTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CategoriesListTtl = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan TagsListTtl = TimeSpan.FromMinutes(10);
}
