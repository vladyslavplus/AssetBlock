using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

public sealed record AnalyticsCollectionItem(
    Guid CollectionId,
    string Title,
    CollectionStatus Status,
    long? Views,
    long? UniqueVisitors,
    long? ItemClicks,
    decimal? ClickThroughRate,
    int AttributedCheckoutStarts,
    int AttributedCompletedOrders,
    long AttributedGrossRevenueCents,
    IReadOnlyList<AnalyticsCollectionTopAsset>? TopClickedAssets);
