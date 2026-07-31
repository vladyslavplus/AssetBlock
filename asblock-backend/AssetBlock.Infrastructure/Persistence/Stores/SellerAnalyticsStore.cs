using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Analytics;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AssetBlock.Infrastructure.Persistence.Stores;

/// <summary>
/// Read-only analytics store. Aggregations and pagination are performed in PostgreSQL.
/// </summary>
internal sealed class SellerAnalyticsStore : ISellerAnalyticsStore
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly bool _disposeContext;

    public SellerAnalyticsStore(IDbContextFactory<ApplicationDbContext> dbFactory)
        : this(dbFactory, disposeContext: true)
    {
    }

    /// <summary>Integration-test constructor that reuses a shared context without disposing of it.</summary>
    internal SellerAnalyticsStore(ApplicationDbContext db)
        : this(new SharedDbContextFactory(db), disposeContext: false)
    {
    }

    private SellerAnalyticsStore(IDbContextFactory<ApplicationDbContext> dbFactory, bool disposeContext)
    {
        _dbFactory = dbFactory;
        _disposeContext = disposeContext;
    }

    public async Task<SellerAnalyticsOverviewSnapshot> GetOverviewSnapshot(
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset comparisonFrom,
        DateTimeOffset comparisonTo,
        int topN,
        AnalyticsGranularity seriesGranularity,
        CancellationToken cancellationToken = default)
    {
        var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await using var tx = await BeginReadOnlyTransaction(db, cancellationToken);

            var currentFacts = await QueryDualPeriodFacts(
                db, sellerId, from, to, comparisonFrom, comparisonTo, cancellationToken);
            var daySeries = await QueryDaySeries(db, sellerId, from, to, cancellationToken);
            var topAssets = await QueryTopAssets(db, sellerId, from, to, topN, cancellationToken);
            var topBundles = await QueryTopBundles(db, sellerId, from, to, topN, cancellationToken);
            var dualRatings = await QueryDualRatings(
                db, sellerId, from, to, comparisonFrom, comparisonTo, cancellationToken);

            var engagementAvailableFrom = await QueryEngagementAvailableFrom(db, sellerId, cancellationToken);

            var currentAvailable = engagementAvailableFrom.HasValue && from >= engagementAvailableFrom.Value;
            var comparisonAvailable = engagementAvailableFrom.HasValue && comparisonFrom >= engagementAvailableFrom.Value;
            var includeEventMetrics = engagementAvailableFrom.HasValue && to > engagementAvailableFrom.Value;

            var commerceFunnel = await QueryCommerceFunnel(db, sellerId, from, to, cancellationToken);
            var trackedCheckoutCoverage = await QueryTrackedCheckoutCoverage(db, sellerId, from, to, cancellationToken);
            var trafficSources = await QueryTrafficSources(db, sellerId, from, to, cancellationToken);
            var externalReferrers = await QueryExternalReferrers(
                db, sellerId, from, to, AnalyticsConstants.MAX_EXTERNAL_REFERRERS, cancellationToken);

            SellerEngagementRawFacts? currentEngagement = null;
            SellerEngagementRawFacts? comparisonEngagement = null;
            AnalyticsTrackedFunnelRaw? trackedFunnel = null;

            if (currentAvailable && comparisonAvailable)
            {
                var dualEngagement = await QueryDualPeriodEngagementFacts(
                    db, sellerId, from, to, comparisonFrom, comparisonTo, cancellationToken);
                currentEngagement = dualEngagement.Current;
                comparisonEngagement = dualEngagement.Comparison;
                trackedFunnel = await QueryTrackedFunnel(db, sellerId, from, to, cancellationToken);
            }
            else
            {
                if (currentAvailable)
                {
                    currentEngagement = await QueryEngagementFacts(db, sellerId, from, to, cancellationToken);
                    trackedFunnel = await QueryTrackedFunnel(db, sellerId, from, to, cancellationToken);
                }

                if (comparisonAvailable)
                {
                    comparisonEngagement = await QueryEngagementFacts(
                        db, sellerId, comparisonFrom, comparisonTo, cancellationToken);
                }
            }

            IReadOnlyList<AnalyticsEngagementDayBucket> engagementSeries = await QueryEngagementSeries(
                db,
                sellerId,
                from,
                to,
                seriesGranularity,
                includeEventMetrics,
                cancellationToken);

            await tx.CommitAsync(cancellationToken);

            return new SellerAnalyticsOverviewSnapshot(
                currentFacts.Current,
                currentFacts.Comparison,
                daySeries,
                topAssets,
                topBundles,
                dualRatings.Current,
                dualRatings.Comparison,
                engagementAvailableFrom,
                currentEngagement,
                comparisonEngagement,
                engagementSeries,
                commerceFunnel,
                trackedFunnel,
                trackedCheckoutCoverage,
                trafficSources,
                externalReferrers);
        }
        finally
        {
            if (_disposeContext)
            {
                await db.DisposeAsync();
            }
        }
    }

    public async Task<(IReadOnlyList<AnalyticsProductRow> Items, int TotalCount)> GetProductsPage(
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsProductTypeFilter productType,
        int page,
        int pageSize,
        AnalyticsProductSort sort,
        AnalyticsSortDirection direction,
        CancellationToken cancellationToken = default)
    {
        var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            if (productType is not (
                AnalyticsProductTypeFilter.ALL or
                AnalyticsProductTypeFilter.ASSET or
                AnalyticsProductTypeFilter.BUNDLE))
            {
                throw new ArgumentOutOfRangeException(nameof(productType), productType, null);
            }

            var includeAssets = productType is AnalyticsProductTypeFilter.ALL or AnalyticsProductTypeFilter.ASSET;
            var includeBundles = productType is AnalyticsProductTypeFilter.ALL or AnalyticsProductTypeFilter.BUNDLE;
            var orderBy = BuildProductsOrderBy(sort, direction);
            var offset = checked((int)((page - 1L) * pageSize));

            var unionParts = new List<string>();
            if (includeAssets)
            {
                unionParts.Add(BuildAssetProductsSelect());
            }

            if (includeBundles)
            {
                unionParts.Add(BuildBundleProductsSelect());
            }

            var productsUnion = string.Join("\nUNION ALL\n", unionParts);
            var sql = BuildProductsPageSql(productsUnion, orderBy);

#pragma warning disable EF1003
            var rows = await db.Database
                .SqlQueryRaw<AnalyticsProductSqlRow>(
                    sql,
                    sellerId,
                    from,
                    to,
                    offset,
                    pageSize)
                .ToListAsync(cancellationToken);
#pragma warning restore EF1003

            if (rows.Count == 0)
            {
                if (page == 1)
                {
                    return ([], 0);
                }

                var countSql = BuildProductsUniverseCountSql(productType);
#pragma warning disable EF1003
                var totalOnly = await db.Database
                    .SqlQueryRaw<ScalarIntSqlRow>(countSql, sellerId)
                    .SingleAsync(cancellationToken);
#pragma warning restore EF1003

                return ([], totalOnly.Value);
            }

            var totalCount = rows[0].TotalCount;
            var items = rows.Select(MapProductRow).ToList();
            return (items, totalCount);
        }
        finally
        {
            if (_disposeContext)
            {
                await db.DisposeAsync();
            }
        }
    }

    public async Task<(IReadOnlyList<AnalyticsSaleRow> Items, bool HasMore)> GetSalesPage(
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsProductTypeFilter productType,
        DateTimeOffset? cursorPurchasedAt,
        Guid? cursorOrderId,
        int pageSize,
        CancellationToken cancellationToken = default)
    {
        var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            var hasCursor = cursorPurchasedAt.HasValue && cursorOrderId.HasValue;
            var sql = SalesPageSql.Build(productType, hasCursor);
            object[] parameters = hasCursor
                ?
                [
                    sellerId,
                    from,
                    to,
                    cursorPurchasedAt!.Value,
                    cursorOrderId!.Value,
                    pageSize + 1
                ]
                : [sellerId, from, to, pageSize + 1];

            var rows = await db.Database.SqlQueryRaw<AnalyticsSaleSqlRow>(sql, parameters)
                .ToListAsync(cancellationToken);

            var hasMore = rows.Count > pageSize;
            var page = hasMore ? rows.Take(pageSize).ToList() : rows;

            var items = page.Select(r =>
            {
                if (r.Units <= 0)
                {
                    throw new InvalidOperationException(
                        $"Order {r.OrderId} matched seller filter but has no seller line stats.");
                }

                return new AnalyticsSaleRow(
                    r.ProductKind == 1 ? AnalyticsProductKind.BUNDLE : AnalyticsProductKind.ASSET,
                    r.ProductId,
                    r.ProductTitle,
                    r.OrderId,
                    r.PurchasedAt,
                    r.Units,
                    r.GrossRevenue);
            }).ToList();

            return (items, hasMore);
        }
        finally
        {
            if (_disposeContext)
            {
                await db.DisposeAsync();
            }
        }
    }

    public Task<ISellerAnalyticsSalesExportSession> OpenSalesExportSession(
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsProductTypeFilter productType,
        CancellationToken cancellationToken = default) =>
        SellerAnalyticsSalesExportSession.OpenAsync(
            _dbFactory,
            sellerId,
            from,
            to,
            productType,
            _disposeContext,
            cancellationToken);

    public async Task<AnalyticsAssetDetailSnapshot?> GetAssetDetail(
        Guid sellerId,
        Guid assetId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsGranularity seriesGranularity,
        CancellationToken cancellationToken = default)
    {
        var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await using var tx = await BeginReadOnlyTransaction(db, cancellationToken);

            var exists = await db.Database
                .SqlQueryRaw<ScalarBoolSqlRow>(AnalyticsProductDetailSql.ASSET_EXISTS, sellerId, assetId)
                .SingleAsync(cancellationToken);
            if (!exists.Value)
            {
                return null;
            }

            var header = await db.Database
                .SqlQueryRaw<AssetDetailHeaderSqlRow>(
                    AnalyticsProductDetailSql.ASSET_DETAIL_HEADER,
                    sellerId,
                    assetId,
                    from,
                    to)
                .SingleAsync(cancellationToken);

            var commerceDaySeries = await QueryDaySeriesForAsset(db, sellerId, assetId, from, to, cancellationToken);
            var engagementAvailableFrom = await QueryEngagementAvailableFrom(db, sellerId, cancellationToken);
            var currentAvailable = engagementAvailableFrom.HasValue && from >= engagementAvailableFrom.Value;
            var includeEventMetrics = engagementAvailableFrom.HasValue && to > engagementAvailableFrom.Value;

            var checkoutStarts = (await db.Database
                .SqlQueryRaw<ScalarIntSqlRow>(
                    AnalyticsProductDetailSql.ASSET_CHECKOUT_STARTS,
                    sellerId,
                    assetId,
                    from,
                    to)
                .SingleAsync(cancellationToken)).Value;

            var completedCheckouts = (await db.Database
                .SqlQueryRaw<ScalarIntSqlRow>(
                    AnalyticsProductDetailSql.ASSET_COMPLETED_CHECKOUTS,
                    sellerId,
                    assetId,
                    from,
                    to)
                .SingleAsync(cancellationToken)).Value;

            long? productViews = null;
            long? uniqueVisitors = null;
            long? downloadRequests = null;
            int? trackedViewSessions = null;
            int? trackedCheckoutSessions = null;
            int? trackedCompletedSessions = null;

            if (currentAvailable)
            {
                var engagementTotals = await db.Database
                    .SqlQueryRaw<ProductEngagementTotalsSqlRow>(
                        AnalyticsProductDetailSql.ASSET_ENGAGEMENT_TOTALS,
                        sellerId,
                        assetId,
                        from,
                        to)
                    .SingleAsync(cancellationToken);

                productViews = engagementTotals.ProductViews;
                uniqueVisitors = engagementTotals.UniqueVisitors;
                downloadRequests = engagementTotals.DownloadRequests;

                var tracked = await db.Database
                    .SqlQueryRaw<TrackedFunnelSqlRow>(
                        AnalyticsProductDetailSql.ASSET_TRACKED_SESSIONS,
                        sellerId,
                        assetId,
                        from,
                        to)
                    .SingleAsync(cancellationToken);

                trackedViewSessions = tracked.ViewSessions;
                trackedCheckoutSessions = tracked.CheckoutSessions;
                trackedCompletedSessions = tracked.CompletedSessions;
            }

            IReadOnlyList<AnalyticsEngagementDayBucket> engagementDaySeries = await QueryProductEngagementSeries(
                db,
                AnalyticsProductDetailSql.ASSET_ENGAGEMENT_DAY_SERIES,
                AnalyticsProductDetailSql.ASSET_ENGAGEMENT_WEEK_SERIES,
                AnalyticsProductDetailSql.ASSET_ENGAGEMENT_MONTH_SERIES,
                sellerId,
                assetId,
                from,
                to,
                seriesGranularity,
                includeEventMetrics,
                cancellationToken);

            await tx.CommitAsync(cancellationToken);

            return new AnalyticsAssetDetailSnapshot(
                header.AssetId,
                header.Title,
                header.IsDeleted,
                header.GrossRevenue,
                header.DirectRevenue,
                header.BundleAllocatedRevenue,
                header.Orders,
                header.UnitsSold,
                header.AverageRating,
                header.ReviewCount,
                header.LatestSaleAt,
                commerceDaySeries,
                engagementAvailableFrom,
                productViews,
                uniqueVisitors,
                checkoutStarts,
                completedCheckouts,
                downloadRequests,
                trackedViewSessions,
                trackedCheckoutSessions,
                trackedCompletedSessions,
                engagementDaySeries);
        }
        finally
        {
            if (_disposeContext)
            {
                await db.DisposeAsync();
            }
        }
    }

    public async Task<AnalyticsBundleDetailSnapshot?> GetBundleDetail(
        Guid sellerId,
        Guid bundleId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsGranularity seriesGranularity,
        CancellationToken cancellationToken = default)
    {
        var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await using var tx = await BeginReadOnlyTransaction(db, cancellationToken);

            var exists = await db.Database
                .SqlQueryRaw<ScalarBoolSqlRow>(AnalyticsProductDetailSql.BUNDLE_EXISTS, sellerId, bundleId)
                .SingleAsync(cancellationToken);
            if (!exists.Value)
            {
                return null;
            }

            var header = await db.Database
                .SqlQueryRaw<BundleDetailHeaderSqlRow>(
                    AnalyticsProductDetailSql.BUNDLE_DETAIL_HEADER,
                    sellerId,
                    bundleId,
                    from,
                    to)
                .SingleAsync(cancellationToken);

            var commerceDaySeries = await QueryDaySeriesForBundle(db, sellerId, bundleId, from, to, cancellationToken);
            var engagementAvailableFrom = await QueryEngagementAvailableFrom(db, sellerId, cancellationToken);
            var currentAvailable = engagementAvailableFrom.HasValue && from >= engagementAvailableFrom.Value;
            var includeEventMetrics = engagementAvailableFrom.HasValue && to > engagementAvailableFrom.Value;

            var checkoutStarts = (await db.Database
                .SqlQueryRaw<ScalarIntSqlRow>(
                    AnalyticsProductDetailSql.BUNDLE_CHECKOUT_STARTS,
                    sellerId,
                    bundleId,
                    from,
                    to)
                .SingleAsync(cancellationToken)).Value;

            var completedCheckouts = (await db.Database
                .SqlQueryRaw<ScalarIntSqlRow>(
                    AnalyticsProductDetailSql.BUNDLE_COMPLETED_CHECKOUTS,
                    sellerId,
                    bundleId,
                    from,
                    to)
                .SingleAsync(cancellationToken)).Value;

            long? productViews = null;
            long? uniqueVisitors = null;
            int? trackedViewSessions = null;
            int? trackedCheckoutSessions = null;
            int? trackedCompletedSessions = null;

            if (currentAvailable)
            {
                var engagementTotals = await db.Database
                    .SqlQueryRaw<BundleEngagementTotalsSqlRow>(
                        AnalyticsProductDetailSql.BUNDLE_ENGAGEMENT_TOTALS,
                        sellerId,
                        bundleId,
                        from,
                        to)
                    .SingleAsync(cancellationToken);

                productViews = engagementTotals.ProductViews;
                uniqueVisitors = engagementTotals.UniqueVisitors;

                var tracked = await db.Database
                    .SqlQueryRaw<TrackedFunnelSqlRow>(
                        AnalyticsProductDetailSql.BUNDLE_TRACKED_SESSIONS,
                        sellerId,
                        bundleId,
                        from,
                        to)
                    .SingleAsync(cancellationToken);

                trackedViewSessions = tracked.ViewSessions;
                trackedCheckoutSessions = tracked.CheckoutSessions;
                trackedCompletedSessions = tracked.CompletedSessions;
            }

            IReadOnlyList<AnalyticsEngagementDayBucket> engagementDaySeries = await QueryProductEngagementSeries(
                db,
                AnalyticsProductDetailSql.BUNDLE_ENGAGEMENT_DAY_SERIES,
                AnalyticsProductDetailSql.BUNDLE_ENGAGEMENT_WEEK_SERIES,
                AnalyticsProductDetailSql.BUNDLE_ENGAGEMENT_MONTH_SERIES,
                sellerId,
                bundleId,
                from,
                to,
                seriesGranularity,
                includeEventMetrics,
                cancellationToken);

            await tx.CommitAsync(cancellationToken);

            return new AnalyticsBundleDetailSnapshot(
                header.BundleId,
                header.Title,
                header.IsArchived,
                header.GrossRevenue,
                header.Orders,
                header.UnitsSold,
                header.CurrentPrice,
                header.ListPriceTotal,
                header.LatestSaleAt,
                commerceDaySeries,
                engagementAvailableFrom,
                productViews,
                uniqueVisitors,
                checkoutStarts,
                completedCheckouts,
                trackedViewSessions,
                trackedCheckoutSessions,
                trackedCompletedSessions,
                engagementDaySeries);
        }
        finally
        {
            if (_disposeContext)
            {
                await db.DisposeAsync();
            }
        }
    }

    public async Task<(IReadOnlyList<AnalyticsCollectionItem> Items, int TotalCount, DateTimeOffset? EngagementAvailableFrom)>
        GetCollectionsPage(
            Guid sellerId,
            DateTimeOffset from,
            DateTimeOffset to,
            int page,
            int pageSize,
            AnalyticsCollectionSort sort,
            AnalyticsSortDirection direction,
            CancellationToken cancellationToken = default)
    {
        var db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await using var tx = await BeginReadOnlyTransaction(db, cancellationToken);

            var orderBy = AnalyticsCollectionsSql.BuildOrderBy(sort, direction);
            var offset = checked((int)((page - 1L) * pageSize));
            var sql = AnalyticsCollectionsSql.BuildCollectionsPageSql(orderBy);

            var rows = await db.Database
                .SqlQueryRaw<CollectionPageSqlRow>(sql, sellerId, from, to, offset, pageSize)
                .ToListAsync(cancellationToken);

            if (rows.Count == 0)
            {
                var engagementAvailableFromEmpty = await QueryEngagementAvailableFrom(db, sellerId, cancellationToken);
                var totalCountEmpty = page == 1
                    ? 0
                    : await QueryCollectionsUniverseCount(db, sellerId, cancellationToken);

                await tx.CommitAsync(cancellationToken);

                return ([], totalCountEmpty, engagementAvailableFromEmpty);
            }

            var totalCount = rows[0].TotalCount;
            var collectionIds = rows.Select(r => r.CollectionId).ToArray();
            var topAssetsByCollection = await QueryTopClickedAssetsForCollections(
                db, sellerId, collectionIds, from, to, cancellationToken);

            var items = rows.Select(r =>
            {
                topAssetsByCollection.TryGetValue(r.CollectionId, out var topAssets);
                var clickThroughRate = r.Views > 0
                    ? decimal.Round((decimal)r.ItemClicks / r.Views, 4, MidpointRounding.AwayFromZero)
                    : (decimal?)null;

                return new AnalyticsCollectionItem(
                    r.CollectionId,
                    r.Title,
                    Enum.Parse<CollectionStatus>(r.Status),
                    r.Views,
                    r.UniqueVisitors,
                    r.ItemClicks,
                    clickThroughRate,
                    r.AttributedCheckoutStarts,
                    r.AttributedCompletedOrders,
                    (long)decimal.Round(r.AttributedGrossRevenue * 100m, 0, MidpointRounding.AwayFromZero),
                    topAssets ?? []);
            }).ToList();

            var engagementAvailableFrom = await QueryEngagementAvailableFrom(db, sellerId, cancellationToken);

            await tx.CommitAsync(cancellationToken);

            return (items, totalCount, engagementAvailableFrom);
        }
        finally
        {
            if (_disposeContext)
            {
                await db.DisposeAsync();
            }
        }
    }

    private static async Task<int> QueryCollectionsUniverseCount(
        ApplicationDbContext db,
        Guid sellerId,
        CancellationToken cancellationToken)
    {
        const string sql = """SELECT COUNT(*)::int AS "Value" FROM collections WHERE "SellerId" = {0}""";
        var row = await db.Database.SqlQueryRaw<ScalarIntSqlRow>(sql, sellerId).SingleAsync(cancellationToken);
        return row.Value;
    }

    private static async Task<Dictionary<Guid, List<AnalyticsCollectionTopAsset>>> QueryTopClickedAssetsForCollections(
        ApplicationDbContext db,
        Guid sellerId,
        Guid[] collectionIds,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        if (collectionIds.Length == 0)
        {
            return [];
        }

        var rows = await db.Database
            .SqlQueryRaw<CollectionTopAssetSqlRow>(
                AnalyticsCollectionsSql.TOP_CLICKED_ASSETS_FOR_COLLECTIONS,
                sellerId,
                collectionIds,
                from,
                to,
                AnalyticsConstants.COLLECTION_TOP_CLICKED_ASSETS)
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(r => r.CollectionId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => new AnalyticsCollectionTopAsset(r.AssetId, r.Title, r.Clicks)).ToList());
    }

    private static async Task<IDbContextTransaction> BeginReadOnlyTransaction(
        ApplicationDbContext db,
        CancellationToken cancellationToken)
    {
        var tx = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        return tx;
    }

    private static async Task<(SellerAnalyticsRawFacts Current, SellerAnalyticsRawFacts Comparison)> QueryDualPeriodFacts(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset comparisonFrom,
        DateTimeOffset comparisonTo,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH seller_first_purchase AS (
                SELECT o."UserId", MIN(o."PurchasedAt") AS first_at
                FROM order_lines ol
                INNER JOIN orders o ON o."Id" = ol."OrderId"
                WHERE ol."SellerId" = {0}
                GROUP BY o."UserId"
            ),
            current_lines AS (
                SELECT
                    ol."OrderId",
                    o."UserId",
                    ol."PricePaid",
                    o."AssetId",
                    o."BundleId"
                FROM order_lines ol
                INNER JOIN orders o ON o."Id" = ol."OrderId"
                WHERE ol."SellerId" = {0}
                  AND o."PurchasedAt" >= {1}
                  AND o."PurchasedAt" < {2}
            ),
            comparison_lines AS (
                SELECT
                    ol."OrderId",
                    o."UserId",
                    ol."PricePaid",
                    o."AssetId",
                    o."BundleId"
                FROM order_lines ol
                INNER JOIN orders o ON o."Id" = ol."OrderId"
                WHERE ol."SellerId" = {0}
                  AND o."PurchasedAt" >= {3}
                  AND o."PurchasedAt" < {4}
            ),
            current_agg AS (
                SELECT
                    COALESCE(SUM("PricePaid"), 0) AS "GrossRevenue",
                    COUNT(DISTINCT "OrderId")::int AS "Orders",
                    COUNT(*)::int AS "Units",
                    COALESCE(SUM(CASE WHEN "AssetId" IS NOT NULL THEN "PricePaid" ELSE 0 END), 0) AS "DirectRevenue",
                    COALESCE(SUM(CASE WHEN "BundleId" IS NOT NULL THEN "PricePaid" ELSE 0 END), 0) AS "BundleRevenue",
                    COUNT(DISTINCT "UserId")::int AS "UniqueCustomers"
                FROM current_lines
            ),
            comparison_agg AS (
                SELECT
                    COALESCE(SUM("PricePaid"), 0) AS "GrossRevenue",
                    COUNT(DISTINCT "OrderId")::int AS "Orders",
                    COUNT(*)::int AS "Units",
                    COALESCE(SUM(CASE WHEN "AssetId" IS NOT NULL THEN "PricePaid" ELSE 0 END), 0) AS "DirectRevenue",
                    COALESCE(SUM(CASE WHEN "BundleId" IS NOT NULL THEN "PricePaid" ELSE 0 END), 0) AS "BundleRevenue",
                    COUNT(DISTINCT "UserId")::int AS "UniqueCustomers"
                FROM comparison_lines
            ),
            current_repeat AS (
                SELECT COUNT(*)::int AS "RepeatCustomers"
                FROM (
                    SELECT "UserId"
                    FROM current_lines
                    GROUP BY "UserId"
                    HAVING COUNT(DISTINCT "OrderId") >= 2
                ) rc
            ),
            comparison_repeat AS (
                SELECT COUNT(*)::int AS "RepeatCustomers"
                FROM (
                    SELECT "UserId"
                    FROM comparison_lines
                    GROUP BY "UserId"
                    HAVING COUNT(DISTINCT "OrderId") >= 2
                ) rc
            ),
            current_new AS (
                SELECT COUNT(*)::int AS "NewCustomers"
                FROM seller_first_purchase bf
                WHERE bf.first_at >= {1} AND bf.first_at < {2}
            ),
            comparison_new AS (
                SELECT COUNT(*)::int AS "NewCustomers"
                FROM seller_first_purchase bf
                WHERE bf.first_at >= {3} AND bf.first_at < {4}
            )
            SELECT
                ca."GrossRevenue" AS "CurrentGrossRevenue",
                ca."Orders" AS "CurrentOrders",
                ca."Units" AS "CurrentUnits",
                ca."DirectRevenue" AS "CurrentDirectRevenue",
                ca."BundleRevenue" AS "CurrentBundleRevenue",
                ca."UniqueCustomers" AS "CurrentUniqueCustomers",
                cn."NewCustomers" AS "CurrentNewCustomers",
                cr."RepeatCustomers" AS "CurrentRepeatCustomers",
                coa."GrossRevenue" AS "ComparisonGrossRevenue",
                coa."Orders" AS "ComparisonOrders",
                coa."Units" AS "ComparisonUnits",
                coa."DirectRevenue" AS "ComparisonDirectRevenue",
                coa."BundleRevenue" AS "ComparisonBundleRevenue",
                coa."UniqueCustomers" AS "ComparisonUniqueCustomers",
                con."NewCustomers" AS "ComparisonNewCustomers",
                cor."RepeatCustomers" AS "ComparisonRepeatCustomers"
            FROM current_agg ca
            CROSS JOIN comparison_agg coa
            CROSS JOIN current_repeat cr
            CROSS JOIN comparison_repeat cor
            CROSS JOIN current_new cn
            CROSS JOIN comparison_new con
            """;

        var row = await db.Database
            .SqlQueryRaw<DualPeriodFactsSqlRow>(sql, sellerId, from, to, comparisonFrom, comparisonTo)
            .SingleAsync(cancellationToken);

        var current = new SellerAnalyticsRawFacts(
            row.CurrentGrossRevenue,
            row.CurrentOrders,
            row.CurrentUnits,
            row.CurrentDirectRevenue,
            row.CurrentBundleRevenue,
            row.CurrentUniqueCustomers,
            row.CurrentNewCustomers,
            row.CurrentRepeatCustomers);

        var comparison = new SellerAnalyticsRawFacts(
            row.ComparisonGrossRevenue,
            row.ComparisonOrders,
            row.ComparisonUnits,
            row.ComparisonDirectRevenue,
            row.ComparisonBundleRevenue,
            row.ComparisonUniqueCustomers,
            row.ComparisonNewCustomers,
            row.ComparisonRepeatCustomers);

        return (current, comparison);
    }

    private static async Task<IReadOnlyList<AnalyticsDayBucket>> QueryDaySeries(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                (o."PurchasedAt" AT TIME ZONE 'UTC')::date AS "SaleDate",
                COALESCE(SUM(ol."PricePaid"), 0) AS "GrossRevenue",
                COUNT(DISTINCT ol."OrderId")::int AS "Orders",
                COUNT(*)::int AS "Units"
            FROM order_lines ol
            INNER JOIN orders o ON o."Id" = ol."OrderId"
            WHERE ol."SellerId" = {0}
              AND o."PurchasedAt" >= {1}
              AND o."PurchasedAt" < {2}
            GROUP BY (o."PurchasedAt" AT TIME ZONE 'UTC')::date
            ORDER BY "SaleDate"
            """;

        var rows = await db.Database
            .SqlQueryRaw<DaySeriesSqlRow>(sql, sellerId, from, to)
            .ToListAsync(cancellationToken);

        return rows
            .Select(r => new AnalyticsDayBucket(r.SaleDate, r.GrossRevenue, r.Orders, r.Units))
            .ToList();
    }

    private static async Task<IReadOnlyList<AnalyticsAssetProductRow>> QueryTopAssets(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        int topN,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH asset_sales AS (
                SELECT
                    ol."AssetId",
                    SUM(ol."PricePaid") AS gross_revenue,
                    SUM(CASE WHEN o."AssetId" IS NOT NULL THEN ol."PricePaid" ELSE 0 END) AS direct_revenue,
                    SUM(CASE WHEN o."BundleId" IS NOT NULL THEN ol."PricePaid" ELSE 0 END) AS bundle_revenue,
                    COUNT(DISTINCT ol."OrderId")::int AS orders,
                    COUNT(*)::int AS units_sold,
                    MAX(o."PurchasedAt") AS latest_sale_at
                FROM order_lines ol
                INNER JOIN orders o ON o."Id" = ol."OrderId"
                WHERE ol."SellerId" = {0}
                  AND o."PurchasedAt" >= {1}
                  AND o."PurchasedAt" < {2}
                GROUP BY ol."AssetId"
                HAVING SUM(ol."PricePaid") > 0
                ORDER BY gross_revenue DESC, ol."AssetId" ASC
                LIMIT {3}
            ),
            ratings AS (
                SELECT
                    r."AssetId",
                    AVG(r."Rating")::float8 AS avg_rating,
                    COUNT(*)::int AS review_count
                FROM reviews r
                INNER JOIN assets a ON a."Id" = r."AssetId"
                WHERE r."AssetId" IN (SELECT "AssetId" FROM asset_sales)
                  AND a."AuthorId" = {0}
                GROUP BY r."AssetId"
            )
            SELECT
                a."Id" AS "AssetId",
                a."Title" AS "Title",
                (a."DeletedAt" IS NOT NULL) AS "IsDeleted",
                s.gross_revenue AS "GrossRevenue",
                s.direct_revenue AS "DirectRevenue",
                s.bundle_revenue AS "BundleAllocatedRevenue",
                s.orders AS "Orders",
                s.units_sold AS "UnitsSold",
                ar.avg_rating AS "AverageRating",
                COALESCE(ar.review_count, 0) AS "ReviewCount",
                s.latest_sale_at AS "LatestSaleAt"
            FROM asset_sales s
            INNER JOIN assets a ON a."Id" = s."AssetId"
            LEFT JOIN ratings ar ON ar."AssetId" = a."Id"
            ORDER BY s.gross_revenue DESC, a."Id" ASC
            """;

        var rows = await db.Database
            .SqlQueryRaw<TopAssetSqlRow>(sql, sellerId, from, to, topN)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new AnalyticsAssetProductRow(
            r.AssetId,
            r.Title,
            r.IsDeleted,
            r.GrossRevenue,
            r.DirectRevenue,
            r.BundleAllocatedRevenue,
            r.Orders,
            r.UnitsSold,
            r.AverageRating,
            r.ReviewCount,
            r.LatestSaleAt)).ToList();
    }

    private static async Task<IReadOnlyList<AnalyticsBundleProductRow>> QueryTopBundles(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        int topN,
        CancellationToken cancellationToken)
    {
        const string sql = """
            WITH seller_bundle_orders AS (
                SELECT
                    o."Id",
                    o."BundleId",
                    o."AmountPaid",
                    o."PurchasedAt",
                    COUNT(*)::int AS units
                FROM order_lines ol
                INNER JOIN orders o ON o."Id" = ol."OrderId"
                WHERE ol."SellerId" = {0}
                  AND o."BundleId" IS NOT NULL
                  AND o."PurchasedAt" >= {1}
                  AND o."PurchasedAt" < {2}
                GROUP BY
                    o."Id",
                    o."BundleId",
                    o."AmountPaid",
                    o."PurchasedAt"
            ),
            bundle_stats AS (
                SELECT
                    "BundleId",
                    SUM("AmountPaid") AS gross_revenue,
                    COUNT(*)::int AS orders,
                    SUM(units)::int AS units_sold,
                    MAX("PurchasedAt") AS latest_sale_at
                FROM seller_bundle_orders
                GROUP BY "BundleId"
                HAVING SUM("AmountPaid") > 0
            )
            SELECT
                b."Id" AS "BundleId",
                COALESCE(br."Title", b."Id"::text) AS "Title",
                (b."ArchivedAt" IS NOT NULL) AS "IsArchived",
                s.gross_revenue AS "GrossRevenue",
                s.orders AS "Orders",
                s.units_sold AS "UnitsSold",
                s.latest_sale_at AS "LatestSaleAt",
                br."Price" AS "CurrentPrice",
                br."ListPriceTotal" AS "ListPriceTotal"
            FROM bundle_stats s
            INNER JOIN bundles b ON b."Id" = s."BundleId"
            LEFT JOIN bundle_revisions br ON br."BundleId" = b."Id" AND br."IsCurrent" = true
            WHERE b."SellerId" = {0}
            ORDER BY s.gross_revenue DESC, b."Id" ASC
            LIMIT {3}
            """;

        var rows = await db.Database
            .SqlQueryRaw<TopBundleSqlRow>(sql, sellerId, from, to, topN)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new AnalyticsBundleProductRow(
            r.BundleId,
            r.Title,
            r.IsArchived,
            r.GrossRevenue,
            r.Orders,
            r.UnitsSold,
            r.LatestSaleAt,
            r.CurrentPrice,
            r.ListPriceTotal)).ToList();
    }

    private static async Task<(SellerRatingsRaw Current, SellerRatingsRaw Comparison)> QueryDualRatings(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        DateTimeOffset comparisonFrom,
        DateTimeOffset comparisonTo,
        CancellationToken cancellationToken)
    {
        const string sql = """
            SELECT
                AVG(r."Rating")::float8 AS "AverageRating",
                COUNT(*) FILTER (
                    WHERE r."CreatedAt" >= {1} AND r."CreatedAt" < {2}
                )::int AS "CurrentNewReviews",
                COUNT(*) FILTER (
                    WHERE r."CreatedAt" >= {3} AND r."CreatedAt" < {4}
                )::int AS "ComparisonNewReviews"
            FROM reviews r
            INNER JOIN assets a ON a."Id" = r."AssetId"
            WHERE a."AuthorId" = {0}
            """;

        var row = await db.Database
            .SqlQueryRaw<DualRatingsSqlRow>(sql, sellerId, from, to, comparisonFrom, comparisonTo)
            .SingleAsync(cancellationToken);

        return (
            new SellerRatingsRaw(row.AverageRating, row.CurrentNewReviews),
            new SellerRatingsRaw(row.AverageRating, row.ComparisonNewReviews));
    }

    private static async Task<DateTimeOffset?> QueryEngagementAvailableFrom(
        ApplicationDbContext db,
        Guid sellerId,
        CancellationToken cancellationToken)
    {
        var row = await db.Database
            .SqlQueryRaw<ScalarDateTimeOffsetSqlRow>(AnalyticsOverviewEngagementSql.ENGAGEMENT_AVAILABLE_FROM, sellerId)
            .SingleAsync(cancellationToken);
        return row.Value;
    }

    private static async Task<SellerEngagementRawFacts> QueryEngagementFacts(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var row = await db.Database
            .SqlQueryRaw<EngagementFactsSqlRow>(
                AnalyticsOverviewEngagementSql.ENGAGEMENT_FACTS,
                sellerId,
                from,
                to)
            .SingleAsync(cancellationToken);

        return new SellerEngagementRawFacts(
            row.ProductViews,
            row.UniqueVisitors,
            row.DownloadRequests,
            row.CollectionViews,
            row.CollectionItemClicks);
    }

    private static async Task<(SellerEngagementRawFacts Current, SellerEngagementRawFacts Comparison)>
        QueryDualPeriodEngagementFacts(
            ApplicationDbContext db,
            Guid sellerId,
            DateTimeOffset from,
            DateTimeOffset to,
            DateTimeOffset comparisonFrom,
            DateTimeOffset comparisonTo,
            CancellationToken cancellationToken)
    {
        var row = await db.Database
            .SqlQueryRaw<DualPeriodEngagementFactsSqlRow>(
                AnalyticsOverviewEngagementSql.DUAL_PERIOD_ENGAGEMENT_FACTS,
                sellerId,
                from,
                to,
                comparisonFrom,
                comparisonTo)
            .SingleAsync(cancellationToken);

        var current = new SellerEngagementRawFacts(
            row.CurrentProductViews,
            row.CurrentUniqueVisitors,
            row.CurrentDownloadRequests,
            row.CurrentCollectionViews,
            row.CurrentCollectionItemClicks);

        var comparison = new SellerEngagementRawFacts(
            row.ComparisonProductViews,
            row.ComparisonUniqueVisitors,
            row.ComparisonDownloadRequests,
            row.ComparisonCollectionViews,
            row.ComparisonCollectionItemClicks);

        return (current, comparison);
    }

    private static async Task<IReadOnlyList<AnalyticsEngagementDayBucket>> QueryEngagementSeries(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsGranularity granularity,
        bool includeEventMetrics,
        CancellationToken cancellationToken)
    {
        var (eventSql, checkoutSql) = granularity switch
        {
            AnalyticsGranularity.WEEK => (
                AnalyticsOverviewEngagementSql.ENGAGEMENT_EVENT_WEEK_SERIES,
                AnalyticsOverviewEngagementSql.ENGAGEMENT_CHECKOUT_WEEK_SERIES),
            AnalyticsGranularity.MONTH => (
                AnalyticsOverviewEngagementSql.ENGAGEMENT_EVENT_MONTH_SERIES,
                AnalyticsOverviewEngagementSql.ENGAGEMENT_CHECKOUT_MONTH_SERIES),
            _ => (
                AnalyticsOverviewEngagementSql.ENGAGEMENT_EVENT_DAY_SERIES,
                AnalyticsOverviewEngagementSql.ENGAGEMENT_CHECKOUT_DAY_SERIES)
        };

        List<EngagementEventDaySeriesSqlRow> eventRows = [];
        if (includeEventMetrics)
        {
            eventRows = await db.Database
                .SqlQueryRaw<EngagementEventDaySeriesSqlRow>(eventSql, sellerId, from, to)
                .ToListAsync(cancellationToken);
        }

        var checkoutRows = await db.Database
            .SqlQueryRaw<EngagementCheckoutDaySeriesSqlRow>(checkoutSql, sellerId, from, to)
            .ToListAsync(cancellationToken);

        var checkoutByBucket = checkoutRows.ToDictionary(r => r.DayUtc);
        var allBuckets = eventRows.Select(r => r.DayUtc)
            .Concat(checkoutRows.Select(r => r.DayUtc))
            .Distinct()
            .OrderBy(d => d)
            .ToList();

        var eventByBucket = eventRows.ToDictionary(r => r.DayUtc);

        return allBuckets.Select(bucket =>
        {
            eventByBucket.TryGetValue(bucket, out var ev);
            checkoutByBucket.TryGetValue(bucket, out var ck);
            return new AnalyticsEngagementDayBucket(
                bucket,
                ev?.ProductViews ?? 0,
                ev?.UniqueVisitors ?? 0,
                ck?.CheckoutStarts ?? 0,
                ck?.CompletedOrders ?? 0,
                ev?.DownloadRequests ?? 0);
        }).ToList();
    }

    private static async Task<IReadOnlyList<AnalyticsEngagementDayBucket>> QueryProductEngagementSeries(
        ApplicationDbContext db,
        string daySeriesSql,
        string weekSeriesSql,
        string monthSeriesSql,
        Guid sellerId,
        Guid productId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsGranularity granularity,
        bool includeEventMetrics,
        CancellationToken cancellationToken)
    {
        var sql = granularity switch
        {
            AnalyticsGranularity.WEEK => weekSeriesSql,
            AnalyticsGranularity.MONTH => monthSeriesSql,
            _ => daySeriesSql
        };

        var rows = await db.Database
            .SqlQueryRaw<EngagementDaySeriesSqlRow>(sql, sellerId, productId, from, to)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new AnalyticsEngagementDayBucket(
            r.DayUtc,
            includeEventMetrics ? r.ProductViews : 0,
            includeEventMetrics ? r.UniqueVisitors : 0,
            r.CheckoutStarts,
            r.CompletedOrders,
            includeEventMetrics ? r.DownloadRequests : 0)).ToList();
    }

    private static async Task<AnalyticsCommerceFunnelRaw> QueryCommerceFunnel(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var row = await db.Database
            .SqlQueryRaw<CommerceFunnelSqlRow>(
                AnalyticsOverviewEngagementSql.COMMERCE_FUNNEL,
                sellerId,
                from,
                to)
            .SingleAsync(cancellationToken);

        return new AnalyticsCommerceFunnelRaw(
            row.CheckoutStarts,
            row.StripeSessionsAttached,
            row.CompletedOrders,
            row.CancelledCheckouts,
            row.PendingCheckouts);
    }

    private static async Task<AnalyticsTrackedFunnelRaw> QueryTrackedFunnel(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var row = await db.Database
            .SqlQueryRaw<TrackedFunnelSqlRow>(
                AnalyticsOverviewEngagementSql.TRACKED_FUNNEL,
                sellerId,
                from,
                to)
            .SingleAsync(cancellationToken);

        return new AnalyticsTrackedFunnelRaw(
            row.ViewSessions,
            row.CheckoutSessions,
            row.CompletedSessions);
    }

    private static async Task<decimal?> QueryTrackedCheckoutCoverage(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var row = await db.Database
            .SqlQueryRaw<ScalarDecimalSqlRow>(
                AnalyticsOverviewEngagementSql.TRACKED_CHECKOUT_COVERAGE,
                sellerId,
                from,
                to)
            .SingleAsync(cancellationToken);
        return row.Value;
    }

    private static async Task<IReadOnlyList<AnalyticsTrafficSourceRaw>> QueryTrafficSources(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQueryRaw<TrafficSourceSqlRow>(
                AnalyticsOverviewEngagementSql.TRAFFIC_SOURCES,
                sellerId,
                from,
                to)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new AnalyticsTrafficSourceRaw(
            Enum.Parse<AnalyticsTrafficSource>(r.Source),
            r.ProductViews,
            r.UniqueVisitors,
            r.CheckoutStarts,
            r.CompletedOrders,
            r.AttributedGrossRevenue)).ToList();
    }

    private static async Task<IReadOnlyList<AnalyticsExternalReferrerRaw>> QueryExternalReferrers(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        int maxReferrers,
        CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQueryRaw<ExternalReferrerSqlRow>(
                AnalyticsOverviewEngagementSql.EXTERNAL_REFERRERS,
                sellerId,
                from,
                to,
                maxReferrers)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new AnalyticsExternalReferrerRaw(
            r.ReferrerHost,
            r.ProductViews,
            r.UniqueVisitors,
            r.CheckoutStarts,
            r.CompletedOrders,
            r.AttributedGrossRevenue)).ToList();
    }

    private static async Task<IReadOnlyList<AnalyticsDayBucket>> QueryDaySeriesForAsset(
        ApplicationDbContext db,
        Guid sellerId,
        Guid assetId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQueryRaw<DaySeriesSqlRow>(
                AnalyticsProductDetailSql.ASSET_COMMERCE_DAY_SERIES,
                sellerId,
                assetId,
                from,
                to)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new AnalyticsDayBucket(r.SaleDate, r.GrossRevenue, r.Orders, r.Units)).ToList();
    }

    private static async Task<IReadOnlyList<AnalyticsDayBucket>> QueryDaySeriesForBundle(
        ApplicationDbContext db,
        Guid sellerId,
        Guid bundleId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        var rows = await db.Database
            .SqlQueryRaw<DaySeriesSqlRow>(
                AnalyticsProductDetailSql.BUNDLE_COMMERCE_DAY_SERIES,
                sellerId,
                bundleId,
                from,
                to)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new AnalyticsDayBucket(r.SaleDate, r.GrossRevenue, r.Orders, r.Units)).ToList();
    }

    private static string BuildProductsPageSql(string productsUnion, string orderBy) =>
        "WITH products AS (\n" + productsUnion + "\n),\n" +
        """
        counted AS (
            SELECT *, COUNT(*) OVER()::int AS "TotalCount"
            FROM products
        )
        SELECT
            "ProductKind",
            "ProductId",
            "Title",
            "IsDeletedOrArchived",
            "GrossRevenue",
            "DirectRevenue",
            "BundleAllocatedRevenue",
            "Orders",
            "UnitsSold",
            "AverageRating",
            "ReviewCount",
            "LatestSaleAt",
            "CurrentPrice",
            "ListPriceTotal",
            "TotalCount"
        FROM counted
        ORDER BY
        """ + orderBy + "\nOFFSET {3} LIMIT {4}";

    private static string BuildProductsUniverseCountSql(AnalyticsProductTypeFilter productType) =>
        productType switch
        {
            AnalyticsProductTypeFilter.ASSET =>
                """SELECT COUNT(*)::int AS "Value" FROM assets WHERE "AuthorId" = {0}""",
            AnalyticsProductTypeFilter.BUNDLE =>
                """SELECT COUNT(*)::int AS "Value" FROM bundles WHERE "SellerId" = {0}""",
            AnalyticsProductTypeFilter.ALL =>
                """
                SELECT (
                    (SELECT COUNT(*) FROM assets WHERE "AuthorId" = {0})
                    +
                    (SELECT COUNT(*) FROM bundles WHERE "SellerId" = {0})
                )::int AS "Value"
                """,
            _ => throw new ArgumentOutOfRangeException(nameof(productType), productType, null)
        };

    private static string BuildAssetProductsSelect() =>
        """
        SELECT
            0 AS "ProductKind",
            a."Id" AS "ProductId",
            a."Title" AS "Title",
            (a."DeletedAt" IS NOT NULL) AS "IsDeletedOrArchived",
            COALESCE(sa."GrossRevenue", 0) AS "GrossRevenue",
            COALESCE(sa."DirectRevenue", 0) AS "DirectRevenue",
            COALESCE(sa."BundleAllocatedRevenue", 0) AS "BundleAllocatedRevenue",
            COALESCE(sa."Orders", 0) AS "Orders",
            COALESCE(sa."UnitsSold", 0) AS "UnitsSold",
            ar."AverageRating" AS "AverageRating",
            COALESCE(ar."ReviewCount", 0) AS "ReviewCount",
            sa."LatestSaleAt" AS "LatestSaleAt",
            NULL::numeric AS "CurrentPrice",
            NULL::numeric AS "ListPriceTotal"
        FROM assets a
        LEFT JOIN (
            SELECT
                ol."AssetId",
                SUM(ol."PricePaid") AS "GrossRevenue",
                SUM(CASE WHEN o."AssetId" IS NOT NULL THEN ol."PricePaid" ELSE 0 END) AS "DirectRevenue",
                SUM(CASE WHEN o."BundleId" IS NOT NULL THEN ol."PricePaid" ELSE 0 END) AS "BundleAllocatedRevenue",
                COUNT(DISTINCT ol."OrderId")::int AS "Orders",
                COUNT(*)::int AS "UnitsSold",
                MAX(o."PurchasedAt") AS "LatestSaleAt"
            FROM order_lines ol
            INNER JOIN orders o ON o."Id" = ol."OrderId"
            WHERE ol."SellerId" = {0}
              AND o."PurchasedAt" >= {1}
              AND o."PurchasedAt" < {2}
            GROUP BY ol."AssetId"
        ) sa ON sa."AssetId" = a."Id"
        LEFT JOIN (
            SELECT
                r."AssetId",
                AVG(r."Rating")::float8 AS "AverageRating",
                COUNT(*)::int AS "ReviewCount"
            FROM reviews r
            INNER JOIN assets a2 ON a2."Id" = r."AssetId"
            WHERE a2."AuthorId" = {0}
            GROUP BY r."AssetId"
        ) ar ON ar."AssetId" = a."Id"
        WHERE a."AuthorId" = {0}
        """;

    private static string BuildBundleProductsSelect() =>
        """
        SELECT
            1 AS "ProductKind",
            b."Id" AS "ProductId",
            COALESCE(br."Title", b."Id"::text) AS "Title",
            (b."ArchivedAt" IS NOT NULL) AS "IsDeletedOrArchived",
            COALESCE(sb."GrossRevenue", 0) AS "GrossRevenue",
            0::numeric AS "DirectRevenue",
            0::numeric AS "BundleAllocatedRevenue",
            COALESCE(sb."Orders", 0) AS "Orders",
            COALESCE(sb."UnitsSold", 0) AS "UnitsSold",
            NULL::float8 AS "AverageRating",
            0 AS "ReviewCount",
            sb."LatestSaleAt" AS "LatestSaleAt",
            br."Price" AS "CurrentPrice",
            br."ListPriceTotal" AS "ListPriceTotal"
        FROM bundles b
        LEFT JOIN bundle_revisions br ON br."BundleId" = b."Id" AND br."IsCurrent" = true
        LEFT JOIN (
            SELECT
                seller_bundle_orders."BundleId",
                SUM(seller_bundle_orders."AmountPaid") AS "GrossRevenue",
                COUNT(*)::int AS "Orders",
                SUM(seller_bundle_orders.units)::int AS "UnitsSold",
                MAX(seller_bundle_orders."PurchasedAt") AS "LatestSaleAt"
            FROM (
                SELECT
                    o."Id",
                    o."BundleId",
                    o."AmountPaid",
                    o."PurchasedAt",
                    COUNT(*)::int AS units
                FROM order_lines ol
                INNER JOIN orders o ON o."Id" = ol."OrderId"
                WHERE ol."SellerId" = {0}
                  AND o."BundleId" IS NOT NULL
                  AND o."PurchasedAt" >= {1}
                  AND o."PurchasedAt" < {2}
                GROUP BY
                    o."Id",
                    o."BundleId",
                    o."AmountPaid",
                    o."PurchasedAt"
            ) seller_bundle_orders
            GROUP BY seller_bundle_orders."BundleId"
        ) sb ON sb."BundleId" = b."Id"
        WHERE b."SellerId" = {0}
        """;

    private static string BuildProductsOrderBy(AnalyticsProductSort sort, AnalyticsSortDirection direction)
    {
        var primary = (sort, direction) switch
        {
            (AnalyticsProductSort.REVENUE, AnalyticsSortDirection.ASC) => """ "GrossRevenue" ASC """,
            (AnalyticsProductSort.REVENUE, AnalyticsSortDirection.DESC) => """ "GrossRevenue" DESC """,
            (AnalyticsProductSort.ORDERS, AnalyticsSortDirection.ASC) => """ "Orders" ASC """,
            (AnalyticsProductSort.ORDERS, AnalyticsSortDirection.DESC) => """ "Orders" DESC """,
            (AnalyticsProductSort.UNITS, AnalyticsSortDirection.ASC) => """ "UnitsSold" ASC """,
            (AnalyticsProductSort.UNITS, AnalyticsSortDirection.DESC) => """ "UnitsSold" DESC """,
            (AnalyticsProductSort.RATING, AnalyticsSortDirection.ASC) => """ "AverageRating" ASC NULLS LAST """,
            (AnalyticsProductSort.RATING, AnalyticsSortDirection.DESC) => """ "AverageRating" DESC NULLS LAST """,
            (AnalyticsProductSort.RECENT, AnalyticsSortDirection.ASC) => """ "LatestSaleAt" ASC NULLS LAST """,
            (AnalyticsProductSort.RECENT, AnalyticsSortDirection.DESC) => """ "LatestSaleAt" DESC NULLS LAST """,
            _ => throw new ArgumentOutOfRangeException(
                nameof(sort),
                sort,
                $"Unsupported products sort combination: {sort}/{direction}")
        };

        return $"{primary}, \"ProductKind\" ASC, \"ProductId\" ASC";
    }

    private static AnalyticsProductRow MapProductRow(AnalyticsProductSqlRow r) =>
        new(
            r.ProductKind == 1 ? AnalyticsProductKind.BUNDLE : AnalyticsProductKind.ASSET,
            r.ProductId,
            r.Title,
            r.IsDeletedOrArchived,
            r.GrossRevenue,
            r.DirectRevenue,
            r.BundleAllocatedRevenue,
            r.Orders,
            r.UnitsSold,
            r.AverageRating,
            r.ReviewCount,
            r.LatestSaleAt,
            r.CurrentPrice,
            r.ListPriceTotal);

    private sealed class SharedDbContextFactory(ApplicationDbContext db) : IDbContextFactory<ApplicationDbContext>
    {
        public ApplicationDbContext CreateDbContext() => db;

        public Task<ApplicationDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(db);
    }
}
