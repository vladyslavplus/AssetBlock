namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Single-store read of all overview raw facts for current and comparison periods.
/// </summary>
public sealed record SellerAnalyticsOverviewSnapshot(
    SellerAnalyticsRawFacts CurrentFacts,
    SellerAnalyticsRawFacts ComparisonFacts,
    IReadOnlyList<AnalyticsDayBucket> DaySeries,
    IReadOnlyList<AnalyticsAssetProductRow> TopAssets,
    IReadOnlyList<AnalyticsBundleProductRow> TopBundles,
    SellerRatingsRaw CurrentRatings,
    SellerRatingsRaw ComparisonRatings);
