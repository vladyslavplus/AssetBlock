namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>Store snapshot for a seller-owned bundle analytics detail page.</summary>
public sealed record AnalyticsBundleDetailSnapshot(
    Guid BundleId,
    string Title,
    bool IsArchived,
    decimal GrossRevenue,
    int Orders,
    int UnitsSold,
    decimal? CurrentPrice,
    decimal? ListPriceTotal,
    DateTimeOffset? LatestSaleAt,
    IReadOnlyList<AnalyticsDayBucket> CommerceDaySeries,
    DateTimeOffset? EngagementAvailableFrom,
    long? ProductViews,
    long? UniqueVisitors,
    int? CheckoutStarts,
    int CompletedCheckouts,
    int? TrackedViewSessions,
    int? TrackedCheckoutSessions,
    int? TrackedCompletedSessions,
    IReadOnlyList<AnalyticsEngagementDayBucket>? EngagementDaySeries);
