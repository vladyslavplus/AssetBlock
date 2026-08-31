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
        ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await using IDbContextTransaction tx = await BeginReadOnlyTransaction(db, cancellationToken);

            (SellerAnalyticsRawFacts CurrentFacts, SellerAnalyticsRawFacts ComparisonFacts, SellerRatingsRaw CurrentRatings, SellerRatingsRaw ComparisonRatings, CommerceContextSqlRow CommerceContext) factsAndCommerce = await QueryOverviewFactsAndCommerceContext(
                db, sellerId, from, to, comparisonFrom, comparisonTo, cancellationToken);
            SellerAnalyticsRawFacts currentFacts = factsAndCommerce.CurrentFacts;
            SellerAnalyticsRawFacts comparisonFacts = factsAndCommerce.ComparisonFacts;
            SellerRatingsRaw currentRatings = factsAndCommerce.CurrentRatings;
            SellerRatingsRaw comparisonRatings = factsAndCommerce.ComparisonRatings;
            CommerceContextSqlRow commerceContext = factsAndCommerce.CommerceContext;

            IReadOnlyList<AnalyticsDayBucket> daySeries = await QueryDaySeries(db, sellerId, from, to, cancellationToken);
            (IReadOnlyList<AnalyticsAssetProductRow> Assets, IReadOnlyList<AnalyticsBundleProductRow> Bundles) topProducts = await QueryTopAssetsAndBundles(db, sellerId, from, to, topN, cancellationToken);
            DateTimeOffset? engagementAvailableFrom = commerceContext.EngagementAvailableFrom;

            var currentAvailable = engagementAvailableFrom.HasValue && from >= engagementAvailableFrom.Value;
            var comparisonAvailable = engagementAvailableFrom.HasValue && comparisonFrom >= engagementAvailableFrom.Value;
            var includeEventMetrics = engagementAvailableFrom.HasValue && to > engagementAvailableFrom.Value;

            var commerceFunnel = new AnalyticsCommerceFunnelRaw(
                commerceContext.CheckoutStarts,
                commerceContext.StripeSessionsAttached,
                commerceContext.CompletedOrders,
                commerceContext.CancelledCheckouts,
                commerceContext.PendingCheckouts);
            var trackedCheckoutCoverage = commerceContext.TrackedCheckoutCoverage;

            SellerEngagementRawFacts? currentEngagement = null;
            SellerEngagementRawFacts? comparisonEngagement = null;
            AnalyticsTrackedFunnelRaw? trackedFunnel = null;

            (IReadOnlyList<AnalyticsTrafficSourceRaw> Sources, IReadOnlyList<AnalyticsExternalReferrerRaw> Referrers) traffic = await QueryTrafficBatch(
                db, sellerId, from, to, AnalyticsConstants.MAX_EXTERNAL_REFERRERS, cancellationToken);
            IReadOnlyList<AnalyticsTrafficSourceRaw> trafficSources = traffic.Sources;
            IReadOnlyList<AnalyticsExternalReferrerRaw> externalReferrers = traffic.Referrers;

            if (currentAvailable && comparisonAvailable)
            {
                (SellerEngagementRawFacts Current, SellerEngagementRawFacts Comparison, AnalyticsTrackedFunnelRaw TrackedFunnel) metrics = await QueryEngagementMetricsDual(
                    db, sellerId, from, to, comparisonFrom, comparisonTo, cancellationToken);
                currentEngagement = metrics.Current;
                comparisonEngagement = metrics.Comparison;
                trackedFunnel = metrics.TrackedFunnel;
            }
            else
            {
                if (currentAvailable)
                {
                    (SellerEngagementRawFacts Engagement, AnalyticsTrackedFunnelRaw TrackedFunnel) metrics = await QueryEngagementMetricsCurrent(db, sellerId, from, to, cancellationToken);
                    currentEngagement = metrics.Engagement;
                    trackedFunnel = metrics.TrackedFunnel;
                }

                if (comparisonAvailable)
                {
                    comparisonEngagement = await QueryEngagementFacts(
                        db, sellerId, comparisonFrom, comparisonTo, cancellationToken);
                }
            }

            IReadOnlyList<AnalyticsEngagementDayBucket> engagementSeries = await QueryEngagementSeriesCombined(
                db,
                sellerId,
                from,
                to,
                seriesGranularity,
                includeEventMetrics,
                cancellationToken);

            await tx.CommitAsync(cancellationToken);

            return new SellerAnalyticsOverviewSnapshot(
                currentFacts,
                comparisonFacts,
                daySeries,
                topProducts.Assets,
                topProducts.Bundles,
                currentRatings,
                comparisonRatings,
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
        ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);
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
            List<AnalyticsProductSqlRow> rows = await db.Database
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
                ScalarIntSqlRow totalOnly = await db.Database
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
        ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);
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

            List<AnalyticsSaleSqlRow> rows = await db.Database.SqlQueryRaw<AnalyticsSaleSqlRow>(sql, parameters)
                .ToListAsync(cancellationToken);

            var hasMore = rows.Count > pageSize;
            List<AnalyticsSaleSqlRow> page = hasMore ? rows.Take(pageSize).ToList() : rows;

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
        ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await using IDbContextTransaction tx = await BeginReadOnlyTransaction(db, cancellationToken);

            ScalarBoolSqlRow exists = await db.Database
                .SqlQueryRaw<ScalarBoolSqlRow>(AnalyticsProductDetailSql.ASSET_EXISTS, sellerId, assetId)
                .SingleAsync(cancellationToken);
            if (!exists.Value)
            {
                return null;
            }

            AssetDetailHeaderSqlRow header = await db.Database
                .SqlQueryRaw<AssetDetailHeaderSqlRow>(
                    AnalyticsProductDetailSql.ASSET_DETAIL_HEADER,
                    sellerId,
                    assetId,
                    from,
                    to)
                .SingleAsync(cancellationToken);

            IReadOnlyList<AnalyticsDayBucket> commerceDaySeries = await QueryDaySeriesForAsset(db, sellerId, assetId, from, to, cancellationToken);
            DateTimeOffset? engagementAvailableFrom = await QueryEngagementAvailableFrom(db, sellerId, cancellationToken);
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
                ProductEngagementTotalsSqlRow engagementTotals = await db.Database
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

                TrackedFunnelSqlRow tracked = await db.Database
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
        ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await using IDbContextTransaction tx = await BeginReadOnlyTransaction(db, cancellationToken);

            ScalarBoolSqlRow exists = await db.Database
                .SqlQueryRaw<ScalarBoolSqlRow>(AnalyticsProductDetailSql.BUNDLE_EXISTS, sellerId, bundleId)
                .SingleAsync(cancellationToken);
            if (!exists.Value)
            {
                return null;
            }

            BundleDetailHeaderSqlRow header = await db.Database
                .SqlQueryRaw<BundleDetailHeaderSqlRow>(
                    AnalyticsProductDetailSql.BUNDLE_DETAIL_HEADER,
                    sellerId,
                    bundleId,
                    from,
                    to)
                .SingleAsync(cancellationToken);

            IReadOnlyList<AnalyticsDayBucket> commerceDaySeries = await QueryDaySeriesForBundle(db, sellerId, bundleId, from, to, cancellationToken);
            DateTimeOffset? engagementAvailableFrom = await QueryEngagementAvailableFrom(db, sellerId, cancellationToken);
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
                BundleEngagementTotalsSqlRow engagementTotals = await db.Database
                    .SqlQueryRaw<BundleEngagementTotalsSqlRow>(
                        AnalyticsProductDetailSql.BUNDLE_ENGAGEMENT_TOTALS,
                        sellerId,
                        bundleId,
                        from,
                        to)
                    .SingleAsync(cancellationToken);

                productViews = engagementTotals.ProductViews;
                uniqueVisitors = engagementTotals.UniqueVisitors;

                TrackedFunnelSqlRow tracked = await db.Database
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
        ApplicationDbContext db = await _dbFactory.CreateDbContextAsync(cancellationToken);
        try
        {
            await using IDbContextTransaction tx = await BeginReadOnlyTransaction(db, cancellationToken);

            var orderBy = AnalyticsCollectionsSql.BuildOrderBy(sort, direction);
            var offset = checked((int)((page - 1L) * pageSize));
            var sql = AnalyticsCollectionsSql.BuildCollectionsPageSql(orderBy);

            List<CollectionPageSqlRow> rows = await db.Database
                .SqlQueryRaw<CollectionPageSqlRow>(sql, sellerId, from, to, offset, pageSize)
                .ToListAsync(cancellationToken);

            if (rows.Count == 0)
            {
                DateTimeOffset? engagementAvailableFromEmpty = await QueryEngagementAvailableFrom(db, sellerId, cancellationToken);
                var totalCountEmpty = page == 1
                    ? 0
                    : await QueryCollectionsUniverseCount(db, sellerId, cancellationToken);

                await tx.CommitAsync(cancellationToken);

                return (new List<AnalyticsCollectionItem>(), totalCountEmpty, engagementAvailableFromEmpty);
            }

            var totalCount = rows[0].TotalCount;
            Guid[] collectionIds = rows.Select(r => r.CollectionId).ToArray();
            Dictionary<Guid, List<AnalyticsCollectionTopAsset>> topAssetsByCollection = await QueryTopClickedAssetsForCollections(
                db, sellerId, collectionIds, from, to, cancellationToken);

            var items = rows.Select(r =>
            {
                topAssetsByCollection.TryGetValue(r.CollectionId, out List<AnalyticsCollectionTopAsset>? topAssets);
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

            DateTimeOffset? engagementAvailableFrom = await QueryEngagementAvailableFrom(db, sellerId, cancellationToken);

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
        ScalarIntSqlRow row = await db.Database.SqlQueryRaw<ScalarIntSqlRow>(sql, sellerId).SingleAsync(cancellationToken);
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

        List<CollectionTopAssetSqlRow> rows = await db.Database
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
        IDbContextTransaction tx = await db.Database.BeginTransactionAsync(
            System.Data.IsolationLevel.RepeatableRead,
            cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY", cancellationToken);
        return tx;
    }

    private static async Task<(
        SellerAnalyticsRawFacts CurrentFacts,
        SellerAnalyticsRawFacts ComparisonFacts,
        SellerRatingsRaw CurrentRatings,
        SellerRatingsRaw ComparisonRatings,
        CommerceContextSqlRow CommerceContext)>
        QueryOverviewFactsAndCommerceContext(
            ApplicationDbContext db,
            Guid sellerId,
            DateTimeOffset from,
            DateTimeOffset to,
            DateTimeOffset comparisonFrom,
            DateTimeOffset comparisonTo,
            CancellationToken cancellationToken)
    {
        OverviewFactsAndCommerceContextSqlRow row = await db.Database
            .SqlQueryRaw<OverviewFactsAndCommerceContextSqlRow>(
                AnalyticsOverviewBatchSql.OVERVIEW_FACTS_AND_COMMERCE_CONTEXT,
                sellerId,
                from,
                to,
                comparisonFrom,
                comparisonTo)
            .SingleAsync(cancellationToken);

        var currentFacts = new SellerAnalyticsRawFacts(
            row.CurrentGrossRevenue,
            row.CurrentOrders,
            row.CurrentUnits,
            row.CurrentDirectRevenue,
            row.CurrentBundleRevenue,
            row.CurrentUniqueCustomers,
            row.CurrentNewCustomers,
            row.CurrentRepeatCustomers);

        var comparisonFacts = new SellerAnalyticsRawFacts(
            row.ComparisonGrossRevenue,
            row.ComparisonOrders,
            row.ComparisonUnits,
            row.ComparisonDirectRevenue,
            row.ComparisonBundleRevenue,
            row.ComparisonUniqueCustomers,
            row.ComparisonNewCustomers,
            row.ComparisonRepeatCustomers);

        var currentRatings = new SellerRatingsRaw(row.AverageRating, row.CurrentNewReviews);
        var comparisonRatings = new SellerRatingsRaw(row.AverageRating, row.ComparisonNewReviews);

        var commerceContext = new CommerceContextSqlRow
        {
            EngagementAvailableFrom = row.EngagementAvailableFrom,
            CheckoutStarts = row.CheckoutStarts,
            StripeSessionsAttached = row.StripeSessionsAttached,
            CompletedOrders = row.CompletedOrders,
            CancelledCheckouts = row.CancelledCheckouts,
            PendingCheckouts = row.PendingCheckouts,
            TrackedCheckoutCoverage = row.TrackedCheckoutCoverage
        };

        return (currentFacts, comparisonFacts, currentRatings, comparisonRatings, commerceContext);
    }

    private static async Task<(SellerEngagementRawFacts Current, SellerEngagementRawFacts Comparison, AnalyticsTrackedFunnelRaw TrackedFunnel)>
        QueryEngagementMetricsDual(
            ApplicationDbContext db,
            Guid sellerId,
            DateTimeOffset from,
            DateTimeOffset to,
            DateTimeOffset comparisonFrom,
            DateTimeOffset comparisonTo,
            CancellationToken cancellationToken)
    {
        EngagementMetricsDualSqlRow row = await db.Database
            .SqlQueryRaw<EngagementMetricsDualSqlRow>(
                AnalyticsOverviewBatchSql.ENGAGEMENT_METRICS_DUAL,
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
        var tracked = new AnalyticsTrackedFunnelRaw(
            row.ViewSessions,
            row.CheckoutSessions,
            row.CompletedSessions);

        return (current, comparison, tracked);
    }

    private static async Task<(SellerEngagementRawFacts Engagement, AnalyticsTrackedFunnelRaw TrackedFunnel)>
        QueryEngagementMetricsCurrent(
            ApplicationDbContext db,
            Guid sellerId,
            DateTimeOffset from,
            DateTimeOffset to,
            CancellationToken cancellationToken)
    {
        EngagementMetricsCurrentSqlRow row = await db.Database
            .SqlQueryRaw<EngagementMetricsCurrentSqlRow>(
                AnalyticsOverviewBatchSql.ENGAGEMENT_METRICS_CURRENT,
                sellerId,
                from,
                to)
            .SingleAsync(cancellationToken);

        var engagement = new SellerEngagementRawFacts(
            row.CurrentProductViews,
            row.CurrentUniqueVisitors,
            row.CurrentDownloadRequests,
            row.CurrentCollectionViews,
            row.CurrentCollectionItemClicks);
        var tracked = new AnalyticsTrackedFunnelRaw(
            row.ViewSessions,
            row.CheckoutSessions,
            row.CompletedSessions);

        return (engagement, tracked);
    }

    private static async Task<(IReadOnlyList<AnalyticsTrafficSourceRaw> Sources, IReadOnlyList<AnalyticsExternalReferrerRaw> Referrers)>
        QueryTrafficBatch(
            ApplicationDbContext db,
            Guid sellerId,
            DateTimeOffset from,
            DateTimeOffset to,
            int maxReferrers,
            CancellationToken cancellationToken)
    {
        List<TrafficUnionSqlRow> rows = await db.Database
            .SqlQueryRaw<TrafficUnionSqlRow>(
                AnalyticsOverviewBatchSql.TRAFFIC_UNION,
                sellerId,
                from,
                to,
                maxReferrers)
            .ToListAsync(cancellationToken);

        var sources = rows
            .Where(r => r.RowKind == "SOURCE")
            .Select(r => new AnalyticsTrafficSourceRaw(
                Enum.Parse<AnalyticsTrafficSource>(r.Key),
                r.ProductViews,
                r.UniqueVisitors,
                r.CheckoutStarts,
                r.CompletedOrders,
                r.AttributedGrossRevenue))
            .ToList();

        var referrers = rows
            .Where(r => r.RowKind == "REFERRER")
            .Select(r => new AnalyticsExternalReferrerRaw(
                r.Key,
                r.ProductViews,
                r.UniqueVisitors,
                r.CheckoutStarts,
                r.CompletedOrders,
                r.AttributedGrossRevenue))
            .ToList();

        return (sources, referrers);
    }

    private static async Task<(IReadOnlyList<AnalyticsAssetProductRow> Assets, IReadOnlyList<AnalyticsBundleProductRow> Bundles)>
        QueryTopAssetsAndBundles(
            ApplicationDbContext db,
            Guid sellerId,
            DateTimeOffset from,
            DateTimeOffset to,
            int topN,
            CancellationToken cancellationToken)
    {
        List<TopProductsUnionSqlRow> rows = await db.Database
            .SqlQueryRaw<TopProductsUnionSqlRow>(
                AnalyticsOverviewBatchSql.TOP_ASSETS_AND_BUNDLES,
                sellerId,
                from,
                to,
                topN)
            .ToListAsync(cancellationToken);

        var assets = rows
            .Where(r => r.ProductKind == "ASSET")
            .OrderByDescending(r => r.GrossRevenue)
            .ThenBy(r => r.ProductId)
            .Select(r => new AnalyticsAssetProductRow(
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
                r.LatestSaleAt))
            .ToList();

        var bundles = rows
            .Where(r => r.ProductKind == "BUNDLE")
            .OrderByDescending(r => r.GrossRevenue)
            .ThenBy(r => r.ProductId)
            .Select(r => new AnalyticsBundleProductRow(
                r.ProductId,
                r.Title,
                r.IsDeletedOrArchived,
                r.GrossRevenue,
                r.Orders,
                r.UnitsSold,
                r.LatestSaleAt,
                r.CurrentPrice,
                r.ListPriceTotal))
            .ToList();

        return (assets, bundles);
    }

    private static async Task<IReadOnlyList<AnalyticsEngagementDayBucket>> QueryEngagementSeriesCombined(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsGranularity granularity,
        bool includeEventMetrics,
        CancellationToken cancellationToken)
    {
        if (!includeEventMetrics)
        {
            var checkoutSql = granularity switch
            {
                AnalyticsGranularity.WEEK => AnalyticsOverviewEngagementSql.ENGAGEMENT_CHECKOUT_WEEK_SERIES,
                AnalyticsGranularity.MONTH => AnalyticsOverviewEngagementSql.ENGAGEMENT_CHECKOUT_MONTH_SERIES,
                _ => AnalyticsOverviewEngagementSql.ENGAGEMENT_CHECKOUT_DAY_SERIES
            };

            List<EngagementCheckoutDaySeriesSqlRow> checkoutRows = await db.Database
                .SqlQueryRaw<EngagementCheckoutDaySeriesSqlRow>(checkoutSql, sellerId, from, to)
                .ToListAsync(cancellationToken);

            return checkoutRows.Select(r => new AnalyticsEngagementDayBucket(
                r.DayUtc,
                0,
                0,
                r.CheckoutStarts,
                r.CompletedOrders,
                0)).ToList();
        }

        var sql = granularity switch
        {
            AnalyticsGranularity.WEEK => AnalyticsOverviewBatchSql.ENGAGEMENT_SERIES_COMBINED_WEEK,
            AnalyticsGranularity.MONTH => AnalyticsOverviewBatchSql.ENGAGEMENT_SERIES_COMBINED_MONTH,
            _ => AnalyticsOverviewBatchSql.ENGAGEMENT_SERIES_COMBINED_DAY
        };

        List<EngagementDaySeriesSqlRow> rows = await db.Database
            .SqlQueryRaw<EngagementDaySeriesSqlRow>(sql, sellerId, from, to)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new AnalyticsEngagementDayBucket(
            r.DayUtc,
            r.ProductViews,
            r.UniqueVisitors,
            r.CheckoutStarts,
            r.CompletedOrders,
            r.DownloadRequests)).ToList();
    }

    private static async Task<SellerEngagementRawFacts> QueryEngagementFacts(
        ApplicationDbContext db,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        EngagementFactsSqlRow row = await db.Database
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

        List<EngagementDaySeriesSqlRow> rows = await db.Database
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
                COUNT(DISTINCT o."Id")::int AS "Orders",
                COUNT(*)::int AS "Units"
            FROM order_lines ol
            INNER JOIN orders o ON o."Id" = ol."OrderId"
            WHERE ol."SellerId" = {0}
              AND o."PurchasedAt" >= {1}
              AND o."PurchasedAt" < {2}
            GROUP BY (o."PurchasedAt" AT TIME ZONE 'UTC')::date
            ORDER BY "SaleDate"
            """;

        List<DaySeriesSqlRow> rows = await db.Database
            .SqlQueryRaw<DaySeriesSqlRow>(sql, sellerId, from, to)
            .ToListAsync(cancellationToken);

        return rows.Select(r => new AnalyticsDayBucket(r.SaleDate, r.GrossRevenue, r.Orders, r.Units)).ToList();
    }

    private static async Task<DateTimeOffset?> QueryEngagementAvailableFrom(
        ApplicationDbContext db,
        Guid sellerId,
        CancellationToken cancellationToken)
    {
        ScalarDateTimeOffsetSqlRow row = await db.Database
            .SqlQueryRaw<ScalarDateTimeOffsetSqlRow>(
                AnalyticsOverviewEngagementSql.ENGAGEMENT_AVAILABLE_FROM,
                sellerId)
            .SingleAsync(cancellationToken);

        return row.Value;
    }

    private static async Task<IReadOnlyList<AnalyticsDayBucket>> QueryDaySeriesForAsset(
        ApplicationDbContext db,
        Guid sellerId,
        Guid assetId,
        DateTimeOffset from,
        DateTimeOffset to,
        CancellationToken cancellationToken)
    {
        List<DaySeriesSqlRow> rows = await db.Database
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
        List<DaySeriesSqlRow> rows = await db.Database
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
