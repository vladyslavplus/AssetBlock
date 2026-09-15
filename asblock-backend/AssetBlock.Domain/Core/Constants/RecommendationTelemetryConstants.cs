namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Purpose-limited recommendation measurement constants. Independent of the 365-day visitor cookie
/// and of analytics_events 400-day retention. Not billing truth.
/// </summary>
public static class RecommendationTelemetryConstants
{
    /// <summary>
    /// Raw recommendation_events older than this many UTC days are eligible for deletion.
    /// Chosen as a bounded measurement window for ranking quality, not inherited from cookie TTL.
    /// </summary>
    public const int RAW_EVENT_RETENTION_DAYS = 90;

    public const int EXPOSURE_TTL_MINUTES = 15;

    public const int RANKING_VERSION_MAX_LENGTH = 64;

    public const int EXPOSURE_TOKEN_LENGTH = 64;

    public const int SLOT_MIN = 0;

    public const int SLOT_MAX = SimilarAssetsConstants.MAX_LIMIT - 1;

    public const string RANKING_VERSION_SIMILAR_METADATA = "similar-assets-v1-metadata";

    public const string RANKING_VERSION_SIMILAR_SEMANTIC = "similar-assets-v1-semantic";

    public const string RANKING_VERSION_SIMILAR_POPULARITY = "similar-assets-v1-popularity";

    public const string RANKING_VERSION_SIMILAR_PERSONAL = "similar-assets-v1-personal";

    /// <summary>Stable pg_try_advisory_lock key for personalization affinity recompute scheduling.</summary>
    public const long PERSONAL_RECOMPUTE_ADVISORY_LOCK_KEY = 0x4153424C4F434B05L;

    /// <summary>
    /// Max opted-in accounts recomputed per worker run. Operational bound, not a data gate:
    /// every skipped account is picked up on a later run, oldest recompute first.
    /// </summary>
    public const int PERSONAL_RECOMPUTE_MAX_USERS_PER_RUN = 25;

    /// <summary>Stable pg_try_advisory_lock key for recommendation daily rollup.</summary>
    public const long DAILY_ROLLUP_ADVISORY_LOCK_KEY = 0x4153424C4F434B03L;

    /// <summary>Stable pg_try_advisory_xact_lock key for recommendation raw-event retention.</summary>
    public const long RETENTION_ADVISORY_LOCK_KEY = 0x4153424C4F434B04L;
}
