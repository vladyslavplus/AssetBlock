using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Read-only analytics data access for seller revenue, customer, and product metrics.
/// All monetary values returned in decimal; conversion to cents is the caller's responsibility.
/// </summary>
public interface ISellerAnalyticsStore
{
    /// <summary>
    /// Returns all overview raw facts in a single consistent read.
    /// </summary>
    Task<SellerAnalyticsOverviewSnapshot> GetOverviewSnapshot(
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset comparisonFrom,
        DateTimeOffset comparisonTo,
        int topN,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paged, SQL-sorted list of seller products (assets and/or bundles) with period metrics.
    /// </summary>
    Task<(IReadOnlyList<AnalyticsProductRow> Items, int TotalCount)> GetProductsPage(
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsProductTypeFilter productType,
        int page,
        int pageSize,
        AnalyticsProductSort sort,
        AnalyticsSortDirection direction,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns keyset-paged sale orders for a seller in [from, to).
    /// Cursor is opaque (PurchasedAt, OrderId) for stable pagination.
    /// </summary>
    Task<(IReadOnlyList<AnalyticsSaleRow> Items, bool HasMore)> GetSalesPage(
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsProductTypeFilter productType,
        DateTimeOffset? cursorPurchasedAt,
        Guid? cursorOrderId,
        int pageSize,
        CancellationToken cancellationToken = default);
}
