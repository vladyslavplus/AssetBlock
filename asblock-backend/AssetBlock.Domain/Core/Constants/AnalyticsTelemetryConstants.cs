namespace AssetBlock.Domain.Core.Constants;

/// <summary>Schema limits shared by engagement telemetry events, checkout attribution, and daily rollups.</summary>
public static class AnalyticsTelemetryConstants
{
    /// <summary>Maximum DNS name length; also the column limit for referrer hosts.</summary>
    public const int REFERRER_HOST_MAX_LENGTH = 253;

    /// <summary>Column width for analytics enums persisted as strings.</summary>
    public const int ENUM_MAX_LENGTH = 32;

    /// <summary>ReferrerHostKey value used by traffic rollup rows that have no external referrer host.</summary>
    public const string REFERRER_HOST_KEY_NONE = "";
}
