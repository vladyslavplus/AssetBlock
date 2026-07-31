using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Traffic source aggregate for overview analytics.
/// ExternalReferrers is populated only when Source is EXTERNAL (max 20 hosts).
/// </summary>
public sealed record AnalyticsTrafficSourceRow(
    AnalyticsTrafficSource Source,
    long ProductViews,
    long UniqueVisitors,
    int CheckoutStarts,
    int CompletedOrders,
    long AttributedGrossRevenueCents,
    IReadOnlyList<AnalyticsExternalReferrerRow>? ExternalReferrers);
