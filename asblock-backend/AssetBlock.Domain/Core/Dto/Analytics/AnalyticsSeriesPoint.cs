namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// A single time-series data point for the analytics chart.
/// BucketStart is the UTC start of the bucket (day/week/month).
/// </summary>
public sealed record AnalyticsSeriesPoint(
    DateTimeOffset BucketStart,
    long GrossRevenueCents,
    int Orders,
    int UnitsSold);
