namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Raw asset product stats as returned by the analytics store.
/// </summary>
public sealed record AnalyticsAssetProductRow(
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
    DateTimeOffset? LatestSaleAt);
