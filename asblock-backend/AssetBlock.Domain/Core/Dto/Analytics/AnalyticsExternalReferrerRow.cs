namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// External traffic breakdown by normalized referrer host.
/// </summary>
public sealed record AnalyticsExternalReferrerRow(
    string ReferrerHost,
    long ProductViews,
    long UniqueVisitors,
    int CheckoutStarts,
    int CompletedOrders,
    long AttributedGrossRevenueCents);
