using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Analytics;

/// <summary>
/// Unified product row returned by the analytics store products page query.
/// </summary>
public sealed record AnalyticsProductRow(
    AnalyticsProductKind ProductKind,
    Guid ProductId,
    string Title,
    bool IsDeletedOrArchived,
    decimal GrossRevenue,
    decimal DirectRevenue,
    decimal BundleAllocatedRevenue,
    int Orders,
    int UnitsSold,
    double? AverageRating,
    int ReviewCount,
    DateTimeOffset? LatestSaleAt,
    decimal? CurrentPrice,
    decimal? ListPriceTotal);
