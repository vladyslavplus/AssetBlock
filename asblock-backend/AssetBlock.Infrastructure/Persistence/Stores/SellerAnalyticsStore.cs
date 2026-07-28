using AssetBlock.Domain.Abstractions.Services;
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

            await tx.CommitAsync(cancellationToken);

            return new SellerAnalyticsOverviewSnapshot(
                currentFacts.Current,
                currentFacts.Comparison,
                daySeries,
                topAssets,
                topBundles,
                dualRatings.Current,
                dualRatings.Comparison);
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
            var offset = checked((int)(((long)page - 1L) * pageSize));

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
