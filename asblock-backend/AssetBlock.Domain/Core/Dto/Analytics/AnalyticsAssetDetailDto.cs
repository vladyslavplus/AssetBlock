using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

public sealed record AnalyticsAssetDetailDto(
    DateOnly From,
    DateOnly To,
    string Timezone,
    DateTimeOffset GeneratedAt,
    string Currency,
    AnalyticsGranularity Granularity,
    DateTimeOffset? EngagementAvailableFrom,
    Guid AssetId,
    string Title,
    AnalyticsProductAvailability Availability,
    long GrossRevenueCents,
    long DirectRevenueCents,
    long BundleAllocatedRevenueCents,
    int Orders,
    int UnitsSold,
    double? AverageRating,
    int ReviewCount,
    DateTimeOffset? LatestSaleAt,
    int CheckoutStarts,
    long? ProductViews,
    long? UniqueVisitors,
    long? DownloadRequests,
    decimal? TrackedViewToCheckoutRate,
    decimal? CheckoutCompletionRate,
    IReadOnlyList<AnalyticsSeriesPoint> Series);
