using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Full overview response for the seller analytics dashboard.
/// </summary>
public sealed record SellerAnalyticsOverviewDto(
    DateOnly From,
    DateOnly To,
    DateOnly ComparisonFrom,
    DateOnly ComparisonTo,
    string Timezone,
    AnalyticsGranularity Granularity,
    DateTimeOffset GeneratedAt,
    string Currency,
    DateTimeOffset? EngagementAvailableFrom,
    MoneyCentsMetric GrossRevenue,
    MoneyCentsMetric DirectRevenue,
    MoneyCentsMetric BundleRevenue,
    CountMetric Orders,
    CountMetric UnitsSold,
    MoneyCentsMetric AverageOrderValue,
    CountMetric UniqueCustomers,
    CountMetric NewCustomers,
    CountMetric ReturningCustomers,
    CountMetric RepeatCustomers,
    RateMetric RepeatCustomerRate,
    double? AverageRating,
    CountMetric NewReviews,
    IReadOnlyList<AnalyticsSeriesPoint> Series,
    IReadOnlyList<AnalyticsProductItem> TopAssets,
    IReadOnlyList<AnalyticsProductItem> TopBundles,
    AnalyticsEngagementTotals? EngagementTotals,
    AnalyticsCommerceFunnel? CommerceFunnel,
    AnalyticsTrackedFunnel? TrackedFunnel,
    decimal? TrackedCheckoutCoverage,
    IReadOnlyList<AnalyticsTrafficSourceRow>? TrafficSources);
