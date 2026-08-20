namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>Store snapshot for a seller-owned asset analytics detail page.</summary>
public sealed record AnalyticsAssetDetailSnapshot(
    Guid AssetId,
    string Title,
    bool IsDeleted,
    decimal GrossRevenue,
    decimal DirectRevenue,
    decimal BundleAllocatedRevenue,
    int Orders,
    int UnitsSold,
    double? AverageRating,
    int ReviewCount,
    DateTimeOffset? LatestSaleAt,
    IReadOnlyList<AnalyticsDayBucket> CommerceDaySeries,
    DateTimeOffset? EngagementAvailableFrom,
    long? ProductViews,
    long? UniqueVisitors,
    int? CheckoutStarts,
    int CompletedCheckouts,
    long? DownloadRequests,
    int? TrackedViewSessions,
    int? TrackedCheckoutSessions,
    int? TrackedCompletedSessions,
    IReadOnlyList<AnalyticsEngagementDayBucket>? EngagementDaySeries);
