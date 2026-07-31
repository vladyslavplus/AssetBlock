namespace AssetBlock.Domain.Core.Constants;

/// <summary>Non-configurable analytics aggregation business constants.</summary>
public static class AnalyticsAggregationConstants
{
    /// <summary>Raw telemetry rows older than this many UTC days are eligible for deletion.</summary>
    public const int RAW_EVENT_RETENTION_DAYS = 400;

    /// <summary>
    /// Stable <c>pg_try_advisory_xact_lock</c> key for the daily rollup worker.
    /// ASCII-ish prefix <c>ASBLOCK</c> with a fixed suffix byte.
    /// </summary>
    public const long DAILY_ROLLUP_ADVISORY_LOCK_KEY = 0x4153424C4F434B01L;

    /// <summary>
    /// Stable <c>pg_try_advisory_xact_lock</c> key for raw-event retention batches.
    /// </summary>
    public const long RETENTION_ADVISORY_LOCK_KEY = 0x4153424C4F434B02L;
}
