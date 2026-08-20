namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// An integer-count KPI with period-over-period comparison.
/// </summary>
public sealed record CountMetric(
    long Current,
    long Previous,
    long AbsoluteChange,
    decimal? PercentageChange);
