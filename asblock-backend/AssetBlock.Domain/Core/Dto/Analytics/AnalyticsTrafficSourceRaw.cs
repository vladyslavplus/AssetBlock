using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>Raw traffic source aggregate from the store layer.</summary>
public sealed record AnalyticsTrafficSourceRaw(
    AnalyticsTrafficSource Source,
    long ProductViews,
    long UniqueVisitors,
    int CheckoutStarts,
    int CompletedOrders,
    decimal AttributedGrossRevenue);
