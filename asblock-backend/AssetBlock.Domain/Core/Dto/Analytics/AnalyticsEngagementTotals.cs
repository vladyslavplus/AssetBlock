namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Seller-level engagement KPIs with period-over-period comparison.
/// </summary>
public sealed record AnalyticsEngagementTotals(
    EngagementCountMetric ProductViews,
    EngagementCountMetric UniqueVisitors,
    EngagementCountMetric DownloadRequests,
    EngagementCountMetric CollectionViews,
    EngagementCountMetric CollectionItemClicks);
