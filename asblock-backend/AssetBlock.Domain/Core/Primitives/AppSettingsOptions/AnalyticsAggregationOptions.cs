namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class AnalyticsAggregationOptions
{
    public const string SECTION_NAME = "AnalyticsAggregation";

    public bool Enabled { get; set; } = true;

    public int IntervalSeconds { get; set; } = 300;

    public int RetentionBatchSize { get; set; } = 10_000;

    public int MaxRetentionBatchesPerRun { get; set; } = 50;

    public int CommandTimeoutSeconds { get; set; } = 120;
}
