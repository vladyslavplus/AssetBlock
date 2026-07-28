namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Constants for seller analytics query constraints and cache behaviour.
/// </summary>
public static class AnalyticsConstants
{
    public const int MAX_DAYS = 366;

    /// <summary>Cache TTL in seconds for overview and products queries.</summary>
    public const int OVERVIEW_CACHE_TTL_SECONDS = 120;
    public const int PRODUCTS_CACHE_TTL_SECONDS = 120;
    public const int SALES_CACHE_TTL_SECONDS = 30;

    public const int DEFAULT_PRODUCTS_PAGE_SIZE = 20;
    public const int MAX_PRODUCTS_PAGE_SIZE = 100;
    public const int MAX_PRODUCTS_PAGE = 10_000;
    public const int MAX_PRODUCTS_OFFSET = 100_000;
    public const int DEFAULT_SALES_PAGE_SIZE = 25;
    public const int MAX_SALES_PAGE_SIZE = 100;

    public const int MAX_CURSOR_LENGTH = 256;

    /// <summary>Top N products surfaced in the overview.</summary>
    public const int OVERVIEW_TOP_N = 5;

    public const string CURRENCY = "usd";

    /// <summary>Granularity thresholds (inclusive) in days.</summary>
    public const int DAY_GRANULARITY_MAX_DAYS = 45;
    public const int WEEK_GRANULARITY_MAX_DAYS = 180;
}
