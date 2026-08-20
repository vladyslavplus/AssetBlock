namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>Raw external referrer aggregate from the store layer.</summary>
public sealed record AnalyticsExternalReferrerRaw(
    string ReferrerHost,
    long ProductViews,
    long UniqueVisitors,
    int CheckoutStarts,
    int CompletedOrders,
    decimal AttributedGrossRevenue);
