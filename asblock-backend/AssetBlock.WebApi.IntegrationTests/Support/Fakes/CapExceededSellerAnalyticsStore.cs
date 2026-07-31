using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.WebApi.IntegrationTests.Support.Fakes;

internal sealed class CapExceededSellerAnalyticsStore : ISellerAnalyticsStore
{
    public Task<ISellerAnalyticsSalesExportSession> OpenSalesExportSession(
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsProductTypeFilter productType,
        CancellationToken cancellationToken = default) =>
        Task.FromResult<ISellerAnalyticsSalesExportSession>(new ExceedsMaxSalesExportSession());

    public Task<SellerAnalyticsOverviewSnapshot> GetOverviewSnapshot(
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset comparisonFrom,
        DateTimeOffset comparisonTo,
        int topN,
        AnalyticsGranularity seriesGranularity,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<(IReadOnlyList<AnalyticsProductRow> Items, int TotalCount)> GetProductsPage(
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsProductTypeFilter productType,
        int page,
        int pageSize,
        AnalyticsProductSort sort,
        AnalyticsSortDirection direction,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<(IReadOnlyList<AnalyticsSaleRow> Items, bool HasMore)> GetSalesPage(
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsProductTypeFilter productType,
        DateTimeOffset? cursorPurchasedAt,
        Guid? cursorOrderId,
        int pageSize,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AnalyticsAssetDetailSnapshot?> GetAssetDetail(
        Guid sellerId,
        Guid assetId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsGranularity seriesGranularity,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<AnalyticsBundleDetailSnapshot?> GetBundleDetail(
        Guid sellerId,
        Guid bundleId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsGranularity seriesGranularity,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public Task<(IReadOnlyList<AnalyticsCollectionItem> Items, int TotalCount, DateTimeOffset? EngagementAvailableFrom)>
        GetCollectionsPage(
            Guid sellerId,
            DateTimeOffset from,
            DateTimeOffset to,
            int page,
            int pageSize,
            AnalyticsCollectionSort sort,
            AnalyticsSortDirection direction,
            CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

internal sealed class ExceedsMaxSalesExportSession : ISellerAnalyticsSalesExportSession
{
    public bool ExceedsMax => true;

    public IAsyncEnumerable<AnalyticsSalesExportRow> ReadRows(CancellationToken cancellationToken = default) =>
        AsyncEnumerable.Empty<AnalyticsSalesExportRow>();

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
