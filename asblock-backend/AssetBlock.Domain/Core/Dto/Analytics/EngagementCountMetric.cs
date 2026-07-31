namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Engagement KPI count with optional period-over-period comparison when the comparison range is covered.
/// </summary>
public sealed record EngagementCountMetric(
    long Current,
    long? Previous,
    long? AbsoluteChange,
    decimal? PercentageChange);
