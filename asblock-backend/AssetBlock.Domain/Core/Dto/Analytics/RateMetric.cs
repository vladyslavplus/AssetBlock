namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// A rate KPI (0–1 scale) with period-over-period comparison.
/// </summary>
public sealed record RateMetric(
    decimal? Current,
    decimal? Previous,
    decimal? AbsoluteChange,
    decimal? PercentageChange);
