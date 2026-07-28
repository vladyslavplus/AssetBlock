namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// A monetary KPI in integer cents with period-over-period comparison.
/// </summary>
public sealed record MoneyCentsMetric(
    long Current,
    long Previous,
    long AbsoluteChange,
    decimal? PercentageChange);
