using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// A product row for the analytics products page.
/// DirectRevenueCents / BundleAllocatedRevenueCents are set for assets only.
/// CurrentPriceCents / ListPriceCents / DiscountPercent are set for bundles only.
/// AverageRating / ReviewCount are set for assets only.
/// </summary>
public sealed record AnalyticsProductItem(
    AnalyticsProductKind ProductKind,
    Guid ProductId,
    string Title,
    AnalyticsProductAvailability Availability,
    long GrossRevenueCents,
    long? DirectRevenueCents,
    long? BundleAllocatedRevenueCents,
    int Orders,
    int UnitsSold,
    double? AverageRating,
    int? ReviewCount,
    DateTimeOffset? LatestSaleAt,
    long? CurrentPriceCents,
    long? ListPriceCents,
    decimal? DiscountPercent);
