namespace AssetBlock.Domain.Core.Constants;

public static class SimilarAssetsConstants
{
    public const int DEFAULT_LIMIT = 6;
    public const int MIN_LIMIT = 1;
    public const int MAX_LIMIT = 12;
    public const int SHORTLIST_SIZE = 100;

    /// <summary>Default ranking: Batch 1 tag/ratings (or local semantic) similarity.</summary>
    public const string MODE_SIMILARITY = "similarity";

    /// <summary>Phase A non-personal popularity ranking over the same shortlist.</summary>
    public const string MODE_POPULARITY = "popularity";

    /// <summary>
    /// UTC-day window (including today) for additive popularity signals.
    /// Operational initial policy, not a measured optimum.
    /// </summary>
    public const int POPULARITY_WINDOW_DAYS = 30;
}
