using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

public sealed record AnalyticsBundleDetailDto(
    DateOnly From,
    DateOnly To,
    string Timezone,
    DateTimeOffset GeneratedAt,
    string Currency,
    AnalyticsGranularity Granularity,
    DateTimeOffset? EngagementAvailableFrom,
    Guid BundleId,
    string Title,
    AnalyticsProductAvailability Availability,
    long GrossRevenueCents,
    int Orders,
    int UnitsSold,
    long? CurrentPriceCents,
    long? ListPriceCents,
    decimal? DiscountPercent,
    DateTimeOffset? LatestSaleAt,
    int CheckoutStarts,
    long? ProductViews,
    long? UniqueVisitors,
    decimal? TrackedViewToCheckoutRate,
    decimal? CheckoutCompletionRate,
    IReadOnlyList<AnalyticsSeriesPoint> Series);
