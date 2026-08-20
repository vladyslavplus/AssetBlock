namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>Raw engagement facts for one analytics period.</summary>
public sealed record SellerEngagementRawFacts(
    long ProductViews,
    long UniqueVisitors,
    long DownloadRequests,
    long CollectionViews,
    long CollectionItemClicks);
