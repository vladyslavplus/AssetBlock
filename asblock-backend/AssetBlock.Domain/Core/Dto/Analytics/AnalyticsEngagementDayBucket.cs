namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>Per-day engagement bucket used to build overview engagement series.</summary>
public sealed record AnalyticsEngagementDayBucket(
    DateOnly Date,
    long ProductViews,
    long UniqueVisitors,
    int CheckoutStarts,
    int CompletedOrders,
    long DownloadRequests);
