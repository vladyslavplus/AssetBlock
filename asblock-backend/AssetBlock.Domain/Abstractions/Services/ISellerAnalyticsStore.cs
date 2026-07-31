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
        AnalyticsGranularity seriesGranularity,
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

    /// <summary>
    /// Opens a prepared sales export session. Cap detection and row streaming share one SQL read.
    /// </summary>
    Task<ISellerAnalyticsSalesExportSession> OpenSalesExportSession(
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsProductTypeFilter productType,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns asset detail analytics for a seller-owned asset, or null when the asset is not owned.
    /// </summary>
    Task<AnalyticsAssetDetailSnapshot?> GetAssetDetail(
        Guid sellerId,
        Guid assetId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsGranularity seriesGranularity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns bundle detail analytics for a seller-owned bundle, or null when the bundle is not owned.
    /// </summary>
    Task<AnalyticsBundleDetailSnapshot?> GetBundleDetail(
        Guid sellerId,
        Guid bundleId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsGranularity seriesGranularity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Returns a paged, SQL-sorted list of seller collections with period engagement and attribution metrics.
    /// </summary>
    Task<(IReadOnlyList<AnalyticsCollectionItem> Items, int TotalCount, DateTimeOffset? EngagementAvailableFrom)>
        GetCollectionsPage(
            Guid sellerId,
            DateTimeOffset from,
            DateTimeOffset to,
            int page,
            int pageSize,
            AnalyticsCollectionSort sort,
            AnalyticsSortDirection direction,
            CancellationToken cancellationToken = default);
}
