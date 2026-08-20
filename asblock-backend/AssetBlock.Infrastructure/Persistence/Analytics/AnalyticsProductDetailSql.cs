namespace AssetBlock.Infrastructure.Persistence.Analytics;

internal static class AnalyticsProductDetailSql
{
    internal const string ASSET_EXISTS = """
        SELECT EXISTS (
            SELECT 1
            FROM assets a
            WHERE a."Id" = {1}
              AND a."AuthorId" = {0}
        ) AS "Value"
        """;

    internal const string BUNDLE_EXISTS = """
        SELECT EXISTS (
            SELECT 1
            FROM bundles b
            WHERE b."Id" = {1}
              AND b."SellerId" = {0}
        ) AS "Value"
        """;

    internal const string ASSET_DETAIL_HEADER = """
        WITH asset_sales AS (
            SELECT
                SUM(ol."PricePaid") AS gross_revenue,
                SUM(CASE WHEN o."AssetId" IS NOT NULL THEN ol."PricePaid" ELSE 0 END) AS direct_revenue,
                SUM(CASE WHEN o."BundleId" IS NOT NULL THEN ol."PricePaid" ELSE 0 END) AS bundle_revenue,
                COUNT(DISTINCT ol."OrderId")::int AS orders,
                COUNT(*)::int AS units_sold,
                MAX(o."PurchasedAt") AS latest_sale_at
            FROM order_lines ol
            INNER JOIN orders o ON o."Id" = ol."OrderId"
            WHERE ol."SellerId" = {0}
              AND ol."AssetId" = {1}
              AND o."PurchasedAt" >= {2}
              AND o."PurchasedAt" < {3}
        ),
        ratings AS (
            SELECT
                AVG(r."Rating")::float8 AS avg_rating,
                COUNT(*)::int AS review_count
            FROM reviews r
            WHERE r."AssetId" = {1}
        )
        SELECT
            a."Id" AS "AssetId",
            a."Title" AS "Title",
            (a."DeletedAt" IS NOT NULL) AS "IsDeleted",
            COALESCE(s.gross_revenue, 0) AS "GrossRevenue",
            COALESCE(s.direct_revenue, 0) AS "DirectRevenue",
            COALESCE(s.bundle_revenue, 0) AS "BundleAllocatedRevenue",
            COALESCE(s.orders, 0) AS "Orders",
            COALESCE(s.units_sold, 0) AS "UnitsSold",
            rt.avg_rating AS "AverageRating",
            COALESCE(rt.review_count, 0) AS "ReviewCount",
            s.latest_sale_at AS "LatestSaleAt"
        FROM assets a
        CROSS JOIN asset_sales s
        LEFT JOIN ratings rt ON true
        WHERE a."Id" = {1}
          AND a."AuthorId" = {0}
        """;

    internal const string ASSET_COMMERCE_DAY_SERIES = """
        SELECT
            (o."PurchasedAt" AT TIME ZONE 'UTC')::date AS "SaleDate",
            COALESCE(SUM(ol."PricePaid"), 0) AS "GrossRevenue",
            COUNT(DISTINCT ol."OrderId")::int AS "Orders",
            COUNT(*)::int AS "Units"
        FROM order_lines ol
        INNER JOIN orders o ON o."Id" = ol."OrderId"
        WHERE ol."SellerId" = {0}
          AND ol."AssetId" = {1}
          AND o."PurchasedAt" >= {2}
          AND o."PurchasedAt" < {3}
        GROUP BY (o."PurchasedAt" AT TIME ZONE 'UTC')::date
        ORDER BY "SaleDate"
        """;

    internal const string ASSET_ENGAGEMENT_TOTALS = """
        SELECT
            COUNT(*) FILTER (WHERE ae."EventType" = 'ASSET_VIEW')::bigint AS "ProductViews",
            COUNT(DISTINCT ae."VisitorId") FILTER (
                WHERE ae."EventType" = 'ASSET_VIEW'
            )::bigint AS "UniqueVisitors",
            COUNT(*) FILTER (WHERE ae."EventType" = 'DOWNLOAD_REQUESTED')::bigint AS "DownloadRequests"
        FROM analytics_events ae
        WHERE ae."SellerId" = {0}
          AND ae."AssetId" = {1}
          AND ae."OccurredAt" >= {2}
          AND ae."OccurredAt" < {3}
        """;

    internal const string ASSET_CHECKOUT_STARTS = """
        SELECT COUNT(DISTINCT ci."Id")::int AS "Value"
        FROM checkout_intents ci
        WHERE EXISTS (
            SELECT 1
            FROM checkout_intent_items cii
            WHERE cii."CheckoutIntentId" = ci."Id"
              AND cii."SellerId" = {0}
        )
          AND ci."AssetId" = {1}
          AND ci."CreatedAt" >= {2}
          AND ci."CreatedAt" < {3}
        """;

    internal const string ASSET_COMPLETED_CHECKOUTS = """
        SELECT COUNT(DISTINCT ci."Id")::int AS "Value"
        FROM checkout_intents ci
        INNER JOIN orders o ON o."CheckoutIntentId" = ci."Id"
        WHERE EXISTS (
            SELECT 1
            FROM checkout_intent_items cii
            WHERE cii."CheckoutIntentId" = ci."Id"
              AND cii."SellerId" = {0}
        )
          AND ci."AssetId" = {1}
          AND ci."CreatedAt" >= {2}
          AND ci."CreatedAt" < {3}
        """;

    internal const string ASSET_TRACKED_SESSIONS = """
        WITH view_sessions AS (
            SELECT DISTINCT ae."SessionId"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."AssetId" = {1}
              AND ae."EventType" = 'ASSET_VIEW'
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
        ),
        checkout_sessions AS (
            SELECT DISTINCT ci."AnalyticsSessionId" AS session_id
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."AssetId" = {1}
              AND ci."AnalyticsSessionId" IS NOT NULL
              AND ci."CreatedAt" >= {2}
              AND ci."CreatedAt" < {3}
              AND ci."AnalyticsSessionId" IN (SELECT "SessionId" FROM view_sessions)
        ),
        completed_sessions AS (
            SELECT DISTINCT ci."AnalyticsSessionId" AS session_id
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            INNER JOIN orders o ON o."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."AssetId" = {1}
              AND ci."AnalyticsSessionId" IS NOT NULL
              AND ci."CreatedAt" >= {2}
              AND ci."CreatedAt" < {3}
              AND ci."AnalyticsSessionId" IN (SELECT session_id FROM checkout_sessions)
        )
        SELECT
            (SELECT COUNT(*)::int FROM view_sessions) AS "ViewSessions",
            (SELECT COUNT(*)::int FROM checkout_sessions) AS "CheckoutSessions",
            (SELECT COUNT(*)::int FROM completed_sessions) AS "CompletedSessions"
        """;

    internal const string ASSET_ENGAGEMENT_DAY_SERIES = """
        WITH rollup AS (
            SELECT
                pad."DayUtc",
                pad."Views" AS "ProductViews",
                pad."UniqueVisitors",
                pad."DownloadRequests"
            FROM product_analytics_daily pad
            WHERE pad."SellerId" = {0}
              AND pad."ProductType" = 'ASSET'
              AND pad."ProductId" = {1}
              AND pad."DayUtc" >= ({2} AT TIME ZONE 'UTC')::date
              AND pad."DayUtc" < ({3} AT TIME ZONE 'UTC')::date
              AND pad."DayUtc" < (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
        ),
        raw AS (
            SELECT
                (ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(*) FILTER (WHERE ae."EventType" = 'ASSET_VIEW')::bigint AS "ProductViews",
                COUNT(DISTINCT ae."VisitorId") FILTER (
                    WHERE ae."EventType" = 'ASSET_VIEW'
                )::bigint AS "UniqueVisitors",
                COUNT(*) FILTER (WHERE ae."EventType" = 'DOWNLOAD_REQUESTED')::bigint AS "DownloadRequests"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."AssetId" = {1}
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
              AND (
                  (ae."OccurredAt" AT TIME ZONE 'UTC')::date = (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
                  OR NOT EXISTS (
                      SELECT 1
                      FROM product_analytics_daily pad
                      WHERE pad."SellerId" = ae."SellerId"
                        AND pad."ProductType" = 'ASSET'
                        AND pad."ProductId" = ae."AssetId"
                        AND pad."DayUtc" = (ae."OccurredAt" AT TIME ZONE 'UTC')::date
                  )
              )
            GROUP BY (ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        event_days AS (
            SELECT "DayUtc" FROM rollup
            UNION
            SELECT "DayUtc" FROM raw
        ),
        merged_events AS (
            SELECT
                d."DayUtc",
                COALESCE(r."ProductViews", raw."ProductViews", 0) AS "ProductViews",
                COALESCE(r."UniqueVisitors", raw."UniqueVisitors", 0) AS "UniqueVisitors",
                COALESCE(r."DownloadRequests", raw."DownloadRequests", 0) AS "DownloadRequests"
            FROM event_days d
            LEFT JOIN rollup r ON r."DayUtc" = d."DayUtc"
            LEFT JOIN raw ON raw."DayUtc" = d."DayUtc" AND r."DayUtc" IS NULL
        ),
        checkout_days AS (
            SELECT
                (ci."CreatedAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(DISTINCT ci."Id")::int AS "CheckoutStarts",
                COUNT(DISTINCT ci."Id") FILTER (
                    WHERE EXISTS (
                        SELECT 1
                        FROM orders o
                        WHERE o."CheckoutIntentId" = ci."Id"
                    )
                )::int AS "CompletedOrders"
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."AssetId" = {1}
              AND ci."CreatedAt" >= {2}
              AND ci."CreatedAt" < {3}
            GROUP BY (ci."CreatedAt" AT TIME ZONE 'UTC')::date
        ),
        days AS (
            SELECT "DayUtc" FROM merged_events
            UNION
            SELECT "DayUtc" FROM checkout_days
        )
        SELECT
            d."DayUtc",
            COALESCE(ed."ProductViews", 0) AS "ProductViews",
            COALESCE(ed."UniqueVisitors", 0) AS "UniqueVisitors",
            COALESCE(cd."CheckoutStarts", 0) AS "CheckoutStarts",
            COALESCE(cd."CompletedOrders", 0) AS "CompletedOrders",
            COALESCE(ed."DownloadRequests", 0) AS "DownloadRequests"
        FROM days d
        LEFT JOIN merged_events ed ON ed."DayUtc" = d."DayUtc"
        LEFT JOIN checkout_days cd ON cd."DayUtc" = d."DayUtc"
        ORDER BY d."DayUtc"
        """;

    internal const string ASSET_ENGAGEMENT_WEEK_SERIES = """
        WITH rollup_days AS (
            SELECT
                pad."DayUtc",
                pad."Views" AS "ProductViews",
                pad."DownloadRequests"
            FROM product_analytics_daily pad
            WHERE pad."SellerId" = {0}
              AND pad."ProductType" = 'ASSET'
              AND pad."ProductId" = {1}
              AND pad."DayUtc" >= ({2} AT TIME ZONE 'UTC')::date
              AND pad."DayUtc" < ({3} AT TIME ZONE 'UTC')::date
              AND pad."DayUtc" < (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
        ),
        raw_days AS (
            SELECT
                (ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(*) FILTER (WHERE ae."EventType" = 'ASSET_VIEW')::bigint AS "ProductViews",
                COUNT(*) FILTER (WHERE ae."EventType" = 'DOWNLOAD_REQUESTED')::bigint AS "DownloadRequests"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."AssetId" = {1}
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
              AND (
                  (ae."OccurredAt" AT TIME ZONE 'UTC')::date = (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
                  OR NOT EXISTS (
                      SELECT 1
                      FROM product_analytics_daily pad
                      WHERE pad."SellerId" = ae."SellerId"
                        AND pad."ProductType" = 'ASSET'
                        AND pad."ProductId" = ae."AssetId"
                        AND pad."DayUtc" = (ae."OccurredAt" AT TIME ZONE 'UTC')::date
                  )
              )
            GROUP BY (ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        day_contributions AS (
            SELECT "DayUtc", "ProductViews", "DownloadRequests" FROM rollup_days
            UNION ALL
            SELECT "DayUtc", "ProductViews", "DownloadRequests" FROM raw_days
        ),
        event_weeks AS (
            SELECT
                date_trunc('week', "DayUtc"::timestamp)::date AS "DayUtc",
                SUM("ProductViews")::bigint AS "ProductViews",
                SUM("DownloadRequests")::bigint AS "DownloadRequests"
            FROM day_contributions
            GROUP BY date_trunc('week', "DayUtc"::timestamp)::date
        ),
        unique_visitors AS (
            SELECT
                date_trunc('week', ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(DISTINCT ae."VisitorId")::bigint AS "UniqueVisitors"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."AssetId" = {1}
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
              AND ae."EventType" = 'ASSET_VIEW'
            GROUP BY date_trunc('week', ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        checkout_weeks AS (
            SELECT
                date_trunc('week', ci."CreatedAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(DISTINCT ci."Id")::int AS "CheckoutStarts",
                COUNT(DISTINCT ci."Id") FILTER (
                    WHERE EXISTS (
                        SELECT 1
                        FROM orders o
                        WHERE o."CheckoutIntentId" = ci."Id"
                    )
                )::int AS "CompletedOrders"
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."AssetId" = {1}
              AND ci."CreatedAt" >= {2}
              AND ci."CreatedAt" < {3}
            GROUP BY date_trunc('week', ci."CreatedAt" AT TIME ZONE 'UTC')::date
        ),
        buckets AS (
            SELECT "DayUtc" FROM event_weeks
            UNION
            SELECT "DayUtc" FROM unique_visitors
            UNION
            SELECT "DayUtc" FROM checkout_weeks
        )
        SELECT
            b."DayUtc",
            COALESCE(ew."ProductViews", 0) AS "ProductViews",
            COALESCE(uv."UniqueVisitors", 0) AS "UniqueVisitors",
            COALESCE(cw."CheckoutStarts", 0) AS "CheckoutStarts",
            COALESCE(cw."CompletedOrders", 0) AS "CompletedOrders",
            COALESCE(ew."DownloadRequests", 0) AS "DownloadRequests"
        FROM buckets b
        LEFT JOIN event_weeks ew ON ew."DayUtc" = b."DayUtc"
        LEFT JOIN unique_visitors uv ON uv."DayUtc" = b."DayUtc"
        LEFT JOIN checkout_weeks cw ON cw."DayUtc" = b."DayUtc"
        ORDER BY b."DayUtc"
        """;

    internal const string ASSET_ENGAGEMENT_MONTH_SERIES = """
        WITH rollup_days AS (
            SELECT
                pad."DayUtc",
                pad."Views" AS "ProductViews",
                pad."DownloadRequests"
            FROM product_analytics_daily pad
            WHERE pad."SellerId" = {0}
              AND pad."ProductType" = 'ASSET'
              AND pad."ProductId" = {1}
              AND pad."DayUtc" >= ({2} AT TIME ZONE 'UTC')::date
              AND pad."DayUtc" < ({3} AT TIME ZONE 'UTC')::date
              AND pad."DayUtc" < (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
        ),
        raw_days AS (
            SELECT
                (ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(*) FILTER (WHERE ae."EventType" = 'ASSET_VIEW')::bigint AS "ProductViews",
                COUNT(*) FILTER (WHERE ae."EventType" = 'DOWNLOAD_REQUESTED')::bigint AS "DownloadRequests"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."AssetId" = {1}
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
              AND (
                  (ae."OccurredAt" AT TIME ZONE 'UTC')::date = (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
                  OR NOT EXISTS (
                      SELECT 1
                      FROM product_analytics_daily pad
                      WHERE pad."SellerId" = ae."SellerId"
                        AND pad."ProductType" = 'ASSET'
                        AND pad."ProductId" = ae."AssetId"
                        AND pad."DayUtc" = (ae."OccurredAt" AT TIME ZONE 'UTC')::date
                  )
              )
            GROUP BY (ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        day_contributions AS (
            SELECT "DayUtc", "ProductViews", "DownloadRequests" FROM rollup_days
            UNION ALL
            SELECT "DayUtc", "ProductViews", "DownloadRequests" FROM raw_days
        ),
        event_months AS (
            SELECT
                date_trunc('month', "DayUtc"::timestamp)::date AS "DayUtc",
                SUM("ProductViews")::bigint AS "ProductViews",
                SUM("DownloadRequests")::bigint AS "DownloadRequests"
            FROM day_contributions
            GROUP BY date_trunc('month', "DayUtc"::timestamp)::date
        ),
        unique_visitors AS (
            SELECT
                date_trunc('month', ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(DISTINCT ae."VisitorId")::bigint AS "UniqueVisitors"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."AssetId" = {1}
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
              AND ae."EventType" = 'ASSET_VIEW'
            GROUP BY date_trunc('month', ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        checkout_months AS (
            SELECT
                date_trunc('month', ci."CreatedAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(DISTINCT ci."Id")::int AS "CheckoutStarts",
                COUNT(DISTINCT ci."Id") FILTER (
                    WHERE EXISTS (
                        SELECT 1
                        FROM orders o
                        WHERE o."CheckoutIntentId" = ci."Id"
                    )
                )::int AS "CompletedOrders"
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."AssetId" = {1}
              AND ci."CreatedAt" >= {2}
              AND ci."CreatedAt" < {3}
            GROUP BY date_trunc('month', ci."CreatedAt" AT TIME ZONE 'UTC')::date
        ),
        buckets AS (
            SELECT "DayUtc" FROM event_months
            UNION
            SELECT "DayUtc" FROM unique_visitors
            UNION
            SELECT "DayUtc" FROM checkout_months
        )
        SELECT
            b."DayUtc",
            COALESCE(em."ProductViews", 0) AS "ProductViews",
            COALESCE(uv."UniqueVisitors", 0) AS "UniqueVisitors",
            COALESCE(cm."CheckoutStarts", 0) AS "CheckoutStarts",
            COALESCE(cm."CompletedOrders", 0) AS "CompletedOrders",
            COALESCE(em."DownloadRequests", 0) AS "DownloadRequests"
        FROM buckets b
        LEFT JOIN event_months em ON em."DayUtc" = b."DayUtc"
        LEFT JOIN unique_visitors uv ON uv."DayUtc" = b."DayUtc"
        LEFT JOIN checkout_months cm ON cm."DayUtc" = b."DayUtc"
        ORDER BY b."DayUtc"
        """;

    internal const string BUNDLE_DETAIL_HEADER = """
        WITH seller_bundle_orders AS (
            SELECT
                o."Id",
                o."AmountPaid",
                o."PurchasedAt",
                COUNT(*)::int AS units
            FROM order_lines ol
            INNER JOIN orders o ON o."Id" = ol."OrderId"
            WHERE ol."SellerId" = {0}
              AND o."BundleId" = {1}
              AND o."PurchasedAt" >= {2}
              AND o."PurchasedAt" < {3}
            GROUP BY o."Id", o."AmountPaid", o."PurchasedAt"
        ),
        bundle_stats AS (
            SELECT
                SUM("AmountPaid") AS gross_revenue,
                COUNT(*)::int AS orders,
                SUM(units)::int AS units_sold,
                MAX("PurchasedAt") AS latest_sale_at
            FROM seller_bundle_orders
        )
        SELECT
            b."Id" AS "BundleId",
            COALESCE(br."Title", b."Id"::text) AS "Title",
            (b."ArchivedAt" IS NOT NULL) AS "IsArchived",
            COALESCE(s.gross_revenue, 0) AS "GrossRevenue",
            COALESCE(s.orders, 0) AS "Orders",
            COALESCE(s.units_sold, 0) AS "UnitsSold",
            s.latest_sale_at AS "LatestSaleAt",
            br."Price" AS "CurrentPrice",
            br."ListPriceTotal" AS "ListPriceTotal"
        FROM bundles b
        LEFT JOIN bundle_revisions br ON br."BundleId" = b."Id" AND br."IsCurrent" = true
        CROSS JOIN bundle_stats s
        WHERE b."Id" = {1}
          AND b."SellerId" = {0}
        """;

    internal const string BUNDLE_COMMERCE_DAY_SERIES = """
        WITH seller_bundle_orders AS (
            SELECT
                o."Id",
                o."AmountPaid",
                o."PurchasedAt",
                COUNT(*)::int AS units
            FROM order_lines ol
            INNER JOIN orders o ON o."Id" = ol."OrderId"
            WHERE ol."SellerId" = {0}
              AND o."BundleId" = {1}
              AND o."PurchasedAt" >= {2}
              AND o."PurchasedAt" < {3}
            GROUP BY o."Id", o."AmountPaid", o."PurchasedAt"
        )
        SELECT
            ("PurchasedAt" AT TIME ZONE 'UTC')::date AS "SaleDate",
            SUM("AmountPaid") AS "GrossRevenue",
            COUNT(*)::int AS "Orders",
            SUM(units)::int AS "Units"
        FROM seller_bundle_orders
        GROUP BY ("PurchasedAt" AT TIME ZONE 'UTC')::date
        ORDER BY "SaleDate"
        """;

    internal const string BUNDLE_ENGAGEMENT_TOTALS = """
        SELECT
            COUNT(*) FILTER (WHERE ae."EventType" = 'BUNDLE_VIEW')::bigint AS "ProductViews",
            COUNT(DISTINCT ae."VisitorId") FILTER (
                WHERE ae."EventType" = 'BUNDLE_VIEW'
            )::bigint AS "UniqueVisitors"
        FROM analytics_events ae
        WHERE ae."SellerId" = {0}
          AND ae."BundleId" = {1}
          AND ae."OccurredAt" >= {2}
          AND ae."OccurredAt" < {3}
        """;

    internal const string BUNDLE_CHECKOUT_STARTS = """
        SELECT COUNT(DISTINCT ci."Id")::int AS "Value"
        FROM checkout_intents ci
        WHERE EXISTS (
            SELECT 1
            FROM checkout_intent_items cii
            WHERE cii."CheckoutIntentId" = ci."Id"
              AND cii."SellerId" = {0}
        )
          AND ci."BundleId" = {1}
          AND ci."CreatedAt" >= {2}
          AND ci."CreatedAt" < {3}
        """;

    internal const string BUNDLE_COMPLETED_CHECKOUTS = """
        SELECT COUNT(DISTINCT ci."Id")::int AS "Value"
        FROM checkout_intents ci
        INNER JOIN orders o ON o."CheckoutIntentId" = ci."Id"
        WHERE EXISTS (
            SELECT 1
            FROM checkout_intent_items cii
            WHERE cii."CheckoutIntentId" = ci."Id"
              AND cii."SellerId" = {0}
        )
          AND ci."BundleId" = {1}
          AND ci."CreatedAt" >= {2}
          AND ci."CreatedAt" < {3}
        """;

    internal const string BUNDLE_TRACKED_SESSIONS = """
        WITH view_sessions AS (
            SELECT DISTINCT ae."SessionId"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."BundleId" = {1}
              AND ae."EventType" = 'BUNDLE_VIEW'
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
        ),
        checkout_sessions AS (
            SELECT DISTINCT ci."AnalyticsSessionId" AS session_id
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."BundleId" = {1}
              AND ci."AnalyticsSessionId" IS NOT NULL
              AND ci."CreatedAt" >= {2}
              AND ci."CreatedAt" < {3}
              AND ci."AnalyticsSessionId" IN (SELECT "SessionId" FROM view_sessions)
        ),
        completed_sessions AS (
            SELECT DISTINCT ci."AnalyticsSessionId" AS session_id
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            INNER JOIN orders o ON o."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."BundleId" = {1}
              AND ci."AnalyticsSessionId" IS NOT NULL
              AND ci."CreatedAt" >= {2}
              AND ci."CreatedAt" < {3}
              AND ci."AnalyticsSessionId" IN (SELECT session_id FROM checkout_sessions)
        )
        SELECT
            (SELECT COUNT(*)::int FROM view_sessions) AS "ViewSessions",
            (SELECT COUNT(*)::int FROM checkout_sessions) AS "CheckoutSessions",
            (SELECT COUNT(*)::int FROM completed_sessions) AS "CompletedSessions"
        """;

    internal const string BUNDLE_ENGAGEMENT_DAY_SERIES = """
        WITH rollup AS (
            SELECT
                pad."DayUtc",
                pad."Views" AS "ProductViews",
                pad."UniqueVisitors"
            FROM product_analytics_daily pad
            WHERE pad."SellerId" = {0}
              AND pad."ProductType" = 'BUNDLE'
              AND pad."ProductId" = {1}
              AND pad."DayUtc" >= ({2} AT TIME ZONE 'UTC')::date
              AND pad."DayUtc" < ({3} AT TIME ZONE 'UTC')::date
              AND pad."DayUtc" < (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
        ),
        raw AS (
            SELECT
                (ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(*) FILTER (WHERE ae."EventType" = 'BUNDLE_VIEW')::bigint AS "ProductViews",
                COUNT(DISTINCT ae."VisitorId") FILTER (
                    WHERE ae."EventType" = 'BUNDLE_VIEW'
                )::bigint AS "UniqueVisitors"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."BundleId" = {1}
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
              AND (
                  (ae."OccurredAt" AT TIME ZONE 'UTC')::date = (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
                  OR NOT EXISTS (
                      SELECT 1
                      FROM product_analytics_daily pad
                      WHERE pad."SellerId" = ae."SellerId"
                        AND pad."ProductType" = 'BUNDLE'
                        AND pad."ProductId" = ae."BundleId"
                        AND pad."DayUtc" = (ae."OccurredAt" AT TIME ZONE 'UTC')::date
                  )
              )
            GROUP BY (ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        event_days AS (
            SELECT "DayUtc" FROM rollup
            UNION
            SELECT "DayUtc" FROM raw
        ),
        merged_events AS (
            SELECT
                d."DayUtc",
                COALESCE(r."ProductViews", raw."ProductViews", 0) AS "ProductViews",
                COALESCE(r."UniqueVisitors", raw."UniqueVisitors", 0) AS "UniqueVisitors"
            FROM event_days d
            LEFT JOIN rollup r ON r."DayUtc" = d."DayUtc"
            LEFT JOIN raw ON raw."DayUtc" = d."DayUtc" AND r."DayUtc" IS NULL
        ),
        checkout_days AS (
            SELECT
                (ci."CreatedAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(DISTINCT ci."Id")::int AS "CheckoutStarts",
                COUNT(DISTINCT ci."Id") FILTER (
                    WHERE EXISTS (
                        SELECT 1
                        FROM orders o
                        WHERE o."CheckoutIntentId" = ci."Id"
                    )
                )::int AS "CompletedOrders"
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."BundleId" = {1}
              AND ci."CreatedAt" >= {2}
              AND ci."CreatedAt" < {3}
            GROUP BY (ci."CreatedAt" AT TIME ZONE 'UTC')::date
        ),
        days AS (
            SELECT "DayUtc" FROM merged_events
            UNION
            SELECT "DayUtc" FROM checkout_days
        )
        SELECT
            d."DayUtc",
            COALESCE(ed."ProductViews", 0) AS "ProductViews",
            COALESCE(ed."UniqueVisitors", 0) AS "UniqueVisitors",
            COALESCE(cd."CheckoutStarts", 0) AS "CheckoutStarts",
            COALESCE(cd."CompletedOrders", 0) AS "CompletedOrders",
            0::bigint AS "DownloadRequests"
        FROM days d
        LEFT JOIN merged_events ed ON ed."DayUtc" = d."DayUtc"
        LEFT JOIN checkout_days cd ON cd."DayUtc" = d."DayUtc"
        ORDER BY d."DayUtc"
        """;

    internal const string BUNDLE_ENGAGEMENT_WEEK_SERIES = """
        WITH rollup_days AS (
            SELECT
                pad."DayUtc",
                pad."Views" AS "ProductViews"
            FROM product_analytics_daily pad
            WHERE pad."SellerId" = {0}
              AND pad."ProductType" = 'BUNDLE'
              AND pad."ProductId" = {1}
              AND pad."DayUtc" >= ({2} AT TIME ZONE 'UTC')::date
              AND pad."DayUtc" < ({3} AT TIME ZONE 'UTC')::date
              AND pad."DayUtc" < (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
        ),
        raw_days AS (
            SELECT
                (ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(*) FILTER (WHERE ae."EventType" = 'BUNDLE_VIEW')::bigint AS "ProductViews"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."BundleId" = {1}
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
              AND (
                  (ae."OccurredAt" AT TIME ZONE 'UTC')::date = (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
                  OR NOT EXISTS (
                      SELECT 1
                      FROM product_analytics_daily pad
                      WHERE pad."SellerId" = ae."SellerId"
                        AND pad."ProductType" = 'BUNDLE'
                        AND pad."ProductId" = ae."BundleId"
                        AND pad."DayUtc" = (ae."OccurredAt" AT TIME ZONE 'UTC')::date
                  )
              )
            GROUP BY (ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        day_contributions AS (
            SELECT "DayUtc", "ProductViews" FROM rollup_days
            UNION ALL
            SELECT "DayUtc", "ProductViews" FROM raw_days
        ),
        event_weeks AS (
            SELECT
                date_trunc('week', "DayUtc"::timestamp)::date AS "DayUtc",
                SUM("ProductViews")::bigint AS "ProductViews"
            FROM day_contributions
            GROUP BY date_trunc('week', "DayUtc"::timestamp)::date
        ),
        unique_visitors AS (
            SELECT
                date_trunc('week', ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(DISTINCT ae."VisitorId")::bigint AS "UniqueVisitors"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."BundleId" = {1}
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
              AND ae."EventType" = 'BUNDLE_VIEW'
            GROUP BY date_trunc('week', ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        checkout_weeks AS (
            SELECT
                date_trunc('week', ci."CreatedAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(DISTINCT ci."Id")::int AS "CheckoutStarts",
                COUNT(DISTINCT ci."Id") FILTER (
                    WHERE EXISTS (
                        SELECT 1
                        FROM orders o
                        WHERE o."CheckoutIntentId" = ci."Id"
                    )
                )::int AS "CompletedOrders"
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."BundleId" = {1}
              AND ci."CreatedAt" >= {2}
              AND ci."CreatedAt" < {3}
            GROUP BY date_trunc('week', ci."CreatedAt" AT TIME ZONE 'UTC')::date
        ),
        buckets AS (
            SELECT "DayUtc" FROM event_weeks
            UNION
            SELECT "DayUtc" FROM unique_visitors
            UNION
            SELECT "DayUtc" FROM checkout_weeks
        )
        SELECT
            b."DayUtc",
            COALESCE(ew."ProductViews", 0) AS "ProductViews",
            COALESCE(uv."UniqueVisitors", 0) AS "UniqueVisitors",
            COALESCE(cw."CheckoutStarts", 0) AS "CheckoutStarts",
            COALESCE(cw."CompletedOrders", 0) AS "CompletedOrders",
            0::bigint AS "DownloadRequests"
        FROM buckets b
        LEFT JOIN event_weeks ew ON ew."DayUtc" = b."DayUtc"
        LEFT JOIN unique_visitors uv ON uv."DayUtc" = b."DayUtc"
        LEFT JOIN checkout_weeks cw ON cw."DayUtc" = b."DayUtc"
        ORDER BY b."DayUtc"
        """;

    internal const string BUNDLE_ENGAGEMENT_MONTH_SERIES = """
        WITH rollup_days AS (
            SELECT
                pad."DayUtc",
                pad."Views" AS "ProductViews"
            FROM product_analytics_daily pad
            WHERE pad."SellerId" = {0}
              AND pad."ProductType" = 'BUNDLE'
              AND pad."ProductId" = {1}
              AND pad."DayUtc" >= ({2} AT TIME ZONE 'UTC')::date
              AND pad."DayUtc" < ({3} AT TIME ZONE 'UTC')::date
              AND pad."DayUtc" < (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
        ),
        raw_days AS (
            SELECT
                (ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(*) FILTER (WHERE ae."EventType" = 'BUNDLE_VIEW')::bigint AS "ProductViews"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."BundleId" = {1}
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
              AND (
                  (ae."OccurredAt" AT TIME ZONE 'UTC')::date = (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
                  OR NOT EXISTS (
                      SELECT 1
                      FROM product_analytics_daily pad
                      WHERE pad."SellerId" = ae."SellerId"
                        AND pad."ProductType" = 'BUNDLE'
                        AND pad."ProductId" = ae."BundleId"
                        AND pad."DayUtc" = (ae."OccurredAt" AT TIME ZONE 'UTC')::date
                  )
              )
            GROUP BY (ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        day_contributions AS (
            SELECT "DayUtc", "ProductViews" FROM rollup_days
            UNION ALL
            SELECT "DayUtc", "ProductViews" FROM raw_days
        ),
        event_months AS (
            SELECT
                date_trunc('month', "DayUtc"::timestamp)::date AS "DayUtc",
                SUM("ProductViews")::bigint AS "ProductViews"
            FROM day_contributions
            GROUP BY date_trunc('month', "DayUtc"::timestamp)::date
        ),
        unique_visitors AS (
            SELECT
                date_trunc('month', ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(DISTINCT ae."VisitorId")::bigint AS "UniqueVisitors"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."BundleId" = {1}
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
              AND ae."EventType" = 'BUNDLE_VIEW'
            GROUP BY date_trunc('month', ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        checkout_months AS (
            SELECT
                date_trunc('month', ci."CreatedAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(DISTINCT ci."Id")::int AS "CheckoutStarts",
                COUNT(DISTINCT ci."Id") FILTER (
                    WHERE EXISTS (
                        SELECT 1
                        FROM orders o
                        WHERE o."CheckoutIntentId" = ci."Id"
                    )
                )::int AS "CompletedOrders"
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."BundleId" = {1}
              AND ci."CreatedAt" >= {2}
              AND ci."CreatedAt" < {3}
            GROUP BY date_trunc('month', ci."CreatedAt" AT TIME ZONE 'UTC')::date
        ),
        buckets AS (
            SELECT "DayUtc" FROM event_months
            UNION
            SELECT "DayUtc" FROM unique_visitors
            UNION
            SELECT "DayUtc" FROM checkout_months
        )
        SELECT
            b."DayUtc",
            COALESCE(em."ProductViews", 0) AS "ProductViews",
            COALESCE(uv."UniqueVisitors", 0) AS "UniqueVisitors",
            COALESCE(cm."CheckoutStarts", 0) AS "CheckoutStarts",
            COALESCE(cm."CompletedOrders", 0) AS "CompletedOrders",
            0::bigint AS "DownloadRequests"
        FROM buckets b
        LEFT JOIN event_months em ON em."DayUtc" = b."DayUtc"
        LEFT JOIN unique_visitors uv ON uv."DayUtc" = b."DayUtc"
        LEFT JOIN checkout_months cm ON cm."DayUtc" = b."DayUtc"
        ORDER BY b."DayUtc"
        """;
}
