namespace AssetBlock.Infrastructure.Persistence.Analytics;

/// <summary>PostgreSQL statements for seller analytics engagement read paths.</summary>
internal static class AnalyticsOverviewEngagementSql
{
    internal const string ENGAGEMENT_AVAILABLE_FROM = """
        SELECT MIN(ae."OccurredAt") AS "Value"
        FROM analytics_events ae
        WHERE ae."SellerId" = {0}
        """;

    internal const string ENGAGEMENT_FACTS = """
        SELECT
            COUNT(*) FILTER (
                WHERE ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
            )::bigint AS "ProductViews",
            COUNT(DISTINCT ae."VisitorId") FILTER (
                WHERE ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
            )::bigint AS "UniqueVisitors",
            COUNT(*) FILTER (WHERE ae."EventType" = 'DOWNLOAD_REQUESTED')::bigint AS "DownloadRequests",
            COUNT(*) FILTER (WHERE ae."EventType" = 'COLLECTION_VIEW')::bigint AS "CollectionViews",
            COUNT(*) FILTER (WHERE ae."EventType" = 'COLLECTION_ITEM_CLICK')::bigint AS "CollectionItemClicks"
        FROM analytics_events ae
        WHERE ae."SellerId" = {0}
          AND ae."OccurredAt" >= {1}
          AND ae."OccurredAt" < {2}
        """;

    internal const string DUAL_PERIOD_ENGAGEMENT_FACTS = """
        WITH current_events AS (
            SELECT ae."EventType", ae."VisitorId"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."OccurredAt" >= {1}
              AND ae."OccurredAt" < {2}
        ),
        comparison_events AS (
            SELECT ae."EventType", ae."VisitorId"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."OccurredAt" >= {3}
              AND ae."OccurredAt" < {4}
        ),
        current_agg AS (
            SELECT
                COUNT(*) FILTER (
                    WHERE "EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
                )::bigint AS "ProductViews",
                COUNT(DISTINCT "VisitorId") FILTER (
                    WHERE "EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
                )::bigint AS "UniqueVisitors",
                COUNT(*) FILTER (WHERE "EventType" = 'DOWNLOAD_REQUESTED')::bigint AS "DownloadRequests",
                COUNT(*) FILTER (WHERE "EventType" = 'COLLECTION_VIEW')::bigint AS "CollectionViews",
                COUNT(*) FILTER (WHERE "EventType" = 'COLLECTION_ITEM_CLICK')::bigint AS "CollectionItemClicks"
            FROM current_events
        ),
        comparison_agg AS (
            SELECT
                COUNT(*) FILTER (
                    WHERE "EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
                )::bigint AS "ProductViews",
                COUNT(DISTINCT "VisitorId") FILTER (
                    WHERE "EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
                )::bigint AS "UniqueVisitors",
                COUNT(*) FILTER (WHERE "EventType" = 'DOWNLOAD_REQUESTED')::bigint AS "DownloadRequests",
                COUNT(*) FILTER (WHERE "EventType" = 'COLLECTION_VIEW')::bigint AS "CollectionViews",
                COUNT(*) FILTER (WHERE "EventType" = 'COLLECTION_ITEM_CLICK')::bigint AS "CollectionItemClicks"
            FROM comparison_events
        )
        SELECT
            ca."ProductViews" AS "CurrentProductViews",
            ca."UniqueVisitors" AS "CurrentUniqueVisitors",
            ca."DownloadRequests" AS "CurrentDownloadRequests",
            ca."CollectionViews" AS "CurrentCollectionViews",
            ca."CollectionItemClicks" AS "CurrentCollectionItemClicks",
            coa."ProductViews" AS "ComparisonProductViews",
            coa."UniqueVisitors" AS "ComparisonUniqueVisitors",
            coa."DownloadRequests" AS "ComparisonDownloadRequests",
            coa."CollectionViews" AS "ComparisonCollectionViews",
            coa."CollectionItemClicks" AS "ComparisonCollectionItemClicks"
        FROM current_agg ca
        CROSS JOIN comparison_agg coa
        """;

    internal const string ENGAGEMENT_EVENT_DAY_SERIES = """
        WITH rollup AS (
            SELECT
                sad."DayUtc",
                sad."AssetViews" + sad."BundleViews" AS "ProductViews",
                sad."UniqueVisitors",
                sad."DownloadRequests"
            FROM seller_analytics_daily sad
            WHERE sad."SellerId" = {0}
              AND sad."DayUtc" >= ({1} AT TIME ZONE 'UTC')::date
              AND sad."DayUtc" < ({2} AT TIME ZONE 'UTC')::date
              AND sad."DayUtc" < (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
        ),
        raw AS (
            SELECT
                (ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(*) FILTER (
                    WHERE ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
                )::bigint AS "ProductViews",
                COUNT(DISTINCT ae."VisitorId") FILTER (
                    WHERE ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
                )::bigint AS "UniqueVisitors",
                COUNT(*) FILTER (WHERE ae."EventType" = 'DOWNLOAD_REQUESTED')::bigint AS "DownloadRequests"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."OccurredAt" >= {1}
              AND ae."OccurredAt" < {2}
              AND (
                  (ae."OccurredAt" AT TIME ZONE 'UTC')::date = (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
                  OR NOT EXISTS (
                      SELECT 1
                      FROM seller_analytics_daily sad
                      WHERE sad."SellerId" = ae."SellerId"
                        AND sad."DayUtc" = (ae."OccurredAt" AT TIME ZONE 'UTC')::date
                  )
              )
            GROUP BY (ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        days AS (
            SELECT "DayUtc" FROM rollup
            UNION
            SELECT "DayUtc" FROM raw
        )
        SELECT
            d."DayUtc",
            COALESCE(r."ProductViews", raw."ProductViews", 0) AS "ProductViews",
            COALESCE(r."UniqueVisitors", raw."UniqueVisitors", 0) AS "UniqueVisitors",
            COALESCE(r."DownloadRequests", raw."DownloadRequests", 0) AS "DownloadRequests"
        FROM days d
        LEFT JOIN rollup r ON r."DayUtc" = d."DayUtc"
        LEFT JOIN raw ON raw."DayUtc" = d."DayUtc" AND r."DayUtc" IS NULL
        ORDER BY d."DayUtc"
        """;

    internal const string ENGAGEMENT_EVENT_WEEK_SERIES = """
        WITH rollup_days AS (
            SELECT
                sad."DayUtc",
                sad."AssetViews" + sad."BundleViews" AS "ProductViews",
                sad."DownloadRequests"
            FROM seller_analytics_daily sad
            WHERE sad."SellerId" = {0}
              AND sad."DayUtc" >= ({1} AT TIME ZONE 'UTC')::date
              AND sad."DayUtc" < ({2} AT TIME ZONE 'UTC')::date
              AND sad."DayUtc" < (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
        ),
        raw_days AS (
            SELECT
                (ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(*) FILTER (
                    WHERE ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
                )::bigint AS "ProductViews",
                COUNT(*) FILTER (WHERE ae."EventType" = 'DOWNLOAD_REQUESTED')::bigint AS "DownloadRequests"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."OccurredAt" >= {1}
              AND ae."OccurredAt" < {2}
              AND (
                  (ae."OccurredAt" AT TIME ZONE 'UTC')::date = (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
                  OR NOT EXISTS (
                      SELECT 1
                      FROM seller_analytics_daily sad
                      WHERE sad."SellerId" = ae."SellerId"
                        AND sad."DayUtc" = (ae."OccurredAt" AT TIME ZONE 'UTC')::date
                  )
              )
            GROUP BY (ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        day_contributions AS (
            SELECT "DayUtc", "ProductViews", "DownloadRequests" FROM rollup_days
            UNION ALL
            SELECT "DayUtc", "ProductViews", "DownloadRequests" FROM raw_days
        ),
        additive AS (
            SELECT
                date_trunc('week', "DayUtc"::timestamp)::date AS "BucketUtc",
                SUM("ProductViews")::bigint AS "ProductViews",
                SUM("DownloadRequests")::bigint AS "DownloadRequests"
            FROM day_contributions
            GROUP BY date_trunc('week', "DayUtc"::timestamp)::date
        ),
        unique_visitors AS (
            SELECT
                date_trunc('week', ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "BucketUtc",
                COUNT(DISTINCT ae."VisitorId")::bigint AS "UniqueVisitors"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."OccurredAt" >= {1}
              AND ae."OccurredAt" < {2}
              AND ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
            GROUP BY date_trunc('week', ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        buckets AS (
            SELECT "BucketUtc" FROM additive
            UNION
            SELECT "BucketUtc" FROM unique_visitors
        )
        SELECT
            b."BucketUtc" AS "DayUtc",
            COALESCE(a."ProductViews", 0) AS "ProductViews",
            COALESCE(uv."UniqueVisitors", 0) AS "UniqueVisitors",
            COALESCE(a."DownloadRequests", 0) AS "DownloadRequests"
        FROM buckets b
        LEFT JOIN additive a ON a."BucketUtc" = b."BucketUtc"
        LEFT JOIN unique_visitors uv ON uv."BucketUtc" = b."BucketUtc"
        ORDER BY b."BucketUtc"
        """;

    internal const string ENGAGEMENT_EVENT_MONTH_SERIES = """
        WITH rollup_days AS (
            SELECT
                sad."DayUtc",
                sad."AssetViews" + sad."BundleViews" AS "ProductViews",
                sad."DownloadRequests"
            FROM seller_analytics_daily sad
            WHERE sad."SellerId" = {0}
              AND sad."DayUtc" >= ({1} AT TIME ZONE 'UTC')::date
              AND sad."DayUtc" < ({2} AT TIME ZONE 'UTC')::date
              AND sad."DayUtc" < (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
        ),
        raw_days AS (
            SELECT
                (ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
                COUNT(*) FILTER (
                    WHERE ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
                )::bigint AS "ProductViews",
                COUNT(*) FILTER (WHERE ae."EventType" = 'DOWNLOAD_REQUESTED')::bigint AS "DownloadRequests"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."OccurredAt" >= {1}
              AND ae."OccurredAt" < {2}
              AND (
                  (ae."OccurredAt" AT TIME ZONE 'UTC')::date = (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
                  OR NOT EXISTS (
                      SELECT 1
                      FROM seller_analytics_daily sad
                      WHERE sad."SellerId" = ae."SellerId"
                        AND sad."DayUtc" = (ae."OccurredAt" AT TIME ZONE 'UTC')::date
                  )
              )
            GROUP BY (ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        day_contributions AS (
            SELECT "DayUtc", "ProductViews", "DownloadRequests" FROM rollup_days
            UNION ALL
            SELECT "DayUtc", "ProductViews", "DownloadRequests" FROM raw_days
        ),
        additive AS (
            SELECT
                date_trunc('month', "DayUtc"::timestamp)::date AS "BucketUtc",
                SUM("ProductViews")::bigint AS "ProductViews",
                SUM("DownloadRequests")::bigint AS "DownloadRequests"
            FROM day_contributions
            GROUP BY date_trunc('month', "DayUtc"::timestamp)::date
        ),
        unique_visitors AS (
            SELECT
                date_trunc('month', ae."OccurredAt" AT TIME ZONE 'UTC')::date AS "BucketUtc",
                COUNT(DISTINCT ae."VisitorId")::bigint AS "UniqueVisitors"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."OccurredAt" >= {1}
              AND ae."OccurredAt" < {2}
              AND ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
            GROUP BY date_trunc('month', ae."OccurredAt" AT TIME ZONE 'UTC')::date
        ),
        buckets AS (
            SELECT "BucketUtc" FROM additive
            UNION
            SELECT "BucketUtc" FROM unique_visitors
        )
        SELECT
            b."BucketUtc" AS "DayUtc",
            COALESCE(a."ProductViews", 0) AS "ProductViews",
            COALESCE(uv."UniqueVisitors", 0) AS "UniqueVisitors",
            COALESCE(a."DownloadRequests", 0) AS "DownloadRequests"
        FROM buckets b
        LEFT JOIN additive a ON a."BucketUtc" = b."BucketUtc"
        LEFT JOIN unique_visitors uv ON uv."BucketUtc" = b."BucketUtc"
        ORDER BY b."BucketUtc"
        """;

    internal const string ENGAGEMENT_CHECKOUT_DAY_SERIES = """
        WITH seller_intents AS (
            SELECT DISTINCT ON (ci."Id")
                ci."Id",
                ci."CreatedAt"
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."CreatedAt" >= {1}
              AND ci."CreatedAt" < {2}
            ORDER BY ci."Id"
        )
        SELECT
            (si."CreatedAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
            COUNT(*)::int AS "CheckoutStarts",
            COUNT(*) FILTER (
                WHERE EXISTS (
                    SELECT 1
                    FROM orders o
                    WHERE o."CheckoutIntentId" = si."Id"
                )
            )::int AS "CompletedOrders"
        FROM seller_intents si
        GROUP BY (si."CreatedAt" AT TIME ZONE 'UTC')::date
        ORDER BY "DayUtc"
        """;

    internal const string ENGAGEMENT_CHECKOUT_WEEK_SERIES = """
        WITH seller_intents AS (
            SELECT DISTINCT ON (ci."Id")
                ci."Id",
                ci."CreatedAt"
            FROM checkout_intents ci
            WHERE EXISTS (
                SELECT 1
                FROM checkout_intent_items cii
                WHERE cii."CheckoutIntentId" = ci."Id"
                  AND cii."SellerId" = {0}
            )
              AND ci."CreatedAt" >= {1}
              AND ci."CreatedAt" < {2}
            ORDER BY ci."Id"
        )
        SELECT
            date_trunc('week', si."CreatedAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
            COUNT(*)::int AS "CheckoutStarts",
            COUNT(*) FILTER (
                WHERE EXISTS (
                    SELECT 1
                    FROM orders o
                    WHERE o."CheckoutIntentId" = si."Id"
                )
            )::int AS "CompletedOrders"
        FROM seller_intents si
        GROUP BY date_trunc('week', si."CreatedAt" AT TIME ZONE 'UTC')::date
        ORDER BY "DayUtc"
        """;

    internal const string ENGAGEMENT_CHECKOUT_MONTH_SERIES = """
        WITH seller_intents AS (
            SELECT DISTINCT ON (ci."Id")
                ci."Id",
                ci."CreatedAt"
            FROM checkout_intents ci
            WHERE EXISTS (
                SELECT 1
                FROM checkout_intent_items cii
                WHERE cii."CheckoutIntentId" = ci."Id"
                  AND cii."SellerId" = {0}
            )
              AND ci."CreatedAt" >= {1}
              AND ci."CreatedAt" < {2}
            ORDER BY ci."Id"
        )
        SELECT
            date_trunc('month', si."CreatedAt" AT TIME ZONE 'UTC')::date AS "DayUtc",
            COUNT(*)::int AS "CheckoutStarts",
            COUNT(*) FILTER (
                WHERE EXISTS (
                    SELECT 1
                    FROM orders o
                    WHERE o."CheckoutIntentId" = si."Id"
                )
            )::int AS "CompletedOrders"
        FROM seller_intents si
        GROUP BY date_trunc('month', si."CreatedAt" AT TIME ZONE 'UTC')::date
        ORDER BY "DayUtc"
        """;

    internal const string COMMERCE_FUNNEL = """
        WITH seller_intents AS (
            SELECT DISTINCT ON (ci."Id")
                ci."Id",
                ci."StripeSessionId",
                ci."Status"
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."CreatedAt" >= {1}
              AND ci."CreatedAt" < {2}
            ORDER BY ci."Id"
        )
        SELECT
            COUNT(*)::int AS "CheckoutStarts",
            COUNT(*) FILTER (WHERE "StripeSessionId" IS NOT NULL)::int AS "StripeSessionsAttached",
            COUNT(*) FILTER (
                WHERE EXISTS (
                    SELECT 1
                    FROM orders o
                    WHERE o."CheckoutIntentId" = seller_intents."Id"
                )
            )::int AS "CompletedOrders",
            COUNT(*) FILTER (WHERE "Status" = 'CANCELLED')::int AS "CancelledCheckouts",
            COUNT(*) FILTER (WHERE "Status" = 'PENDING')::int AS "PendingCheckouts"
        FROM seller_intents
        """;

    internal const string TRACKED_FUNNEL = """
        WITH view_sessions AS (
            SELECT DISTINCT ae."SessionId"
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."OccurredAt" >= {1}
              AND ae."OccurredAt" < {2}
              AND ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
        ),
        seller_checkout_sessions AS (
            SELECT DISTINCT ci."AnalyticsSessionId" AS session_id
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."CreatedAt" >= {1}
              AND ci."CreatedAt" < {2}
              AND ci."AnalyticsSessionId" IS NOT NULL
        ),
        checkout_sessions AS (
            SELECT scs.session_id
            FROM seller_checkout_sessions scs
            INNER JOIN view_sessions vs ON vs."SessionId" = scs.session_id
        ),
        completed_sessions AS (
            SELECT DISTINCT ci."AnalyticsSessionId" AS session_id
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            INNER JOIN orders o ON o."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."CreatedAt" >= {1}
              AND ci."CreatedAt" < {2}
              AND ci."AnalyticsSessionId" IS NOT NULL
              AND ci."AnalyticsSessionId" IN (SELECT session_id FROM checkout_sessions)
        )
        SELECT
            (SELECT COUNT(*)::int FROM view_sessions) AS "ViewSessions",
            (SELECT COUNT(*)::int FROM checkout_sessions) AS "CheckoutSessions",
            (SELECT COUNT(*)::int FROM completed_sessions) AS "CompletedSessions"
        """;

    internal const string TRACKED_CHECKOUT_COVERAGE = """
        WITH seller_intents AS (
            SELECT DISTINCT ON (ci."Id")
                ci."Id",
                ci."AnalyticsSessionId"
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."CreatedAt" >= {1}
              AND ci."CreatedAt" < {2}
            ORDER BY ci."Id"
        )
        SELECT
            CASE
                WHEN COUNT(*) = 0 THEN NULL::numeric
                ELSE COUNT(*) FILTER (WHERE "AnalyticsSessionId" IS NOT NULL)::numeric / COUNT(*)::numeric
            END AS "Value"
        FROM seller_intents
        """;

    internal const string TRAFFIC_SOURCES = """
        WITH rollup_views AS (
            SELECT
                tad."Source"::text AS source,
                SUM(tad."ProductViews")::bigint AS product_views
            FROM traffic_analytics_daily tad
            WHERE tad."SellerId" = {0}
              AND tad."DayUtc" >= ({1} AT TIME ZONE 'UTC')::date
              AND tad."DayUtc" < ({2} AT TIME ZONE 'UTC')::date
              AND tad."DayUtc" < (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
            GROUP BY tad."Source"
        ),
        raw_views AS (
            SELECT
                ae."Source"::text AS source,
                COUNT(*)::bigint AS product_views
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."OccurredAt" >= {1}
              AND ae."OccurredAt" < {2}
              AND ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
              AND (
                  (ae."OccurredAt" AT TIME ZONE 'UTC')::date = (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
                  OR NOT EXISTS (
                      SELECT 1
                      FROM traffic_analytics_daily tad
                      WHERE tad."SellerId" = ae."SellerId"
                        AND tad."DayUtc" = (ae."OccurredAt" AT TIME ZONE 'UTC')::date
                        AND tad."Source" = ae."Source"
                        AND tad."ReferrerHostKey" = CASE
                            WHEN ae."Source" = 'EXTERNAL' THEN COALESCE(ae."ReferrerHost", '')
                            ELSE ''
                        END
                  )
              )
            GROUP BY ae."Source"
        ),
        event_traffic AS (
            SELECT
                source,
                SUM(product_views)::bigint AS product_views
            FROM (
                SELECT source, product_views FROM rollup_views
                UNION ALL
                SELECT source, product_views FROM raw_views
            ) combined
            GROUP BY source
        ),
        event_uv AS (
            SELECT
                ae."Source"::text AS source,
                COUNT(DISTINCT ae."VisitorId")::bigint AS unique_visitors
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."OccurredAt" >= {1}
              AND ae."OccurredAt" < {2}
              AND ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
            GROUP BY ae."Source"
        ),
        seller_intents AS (
            SELECT DISTINCT ON (ci."Id")
                ci."Id",
                ci."AttributionSource"::text AS source
            FROM checkout_intents ci
            WHERE EXISTS (
                SELECT 1
                FROM checkout_intent_items cii
                WHERE cii."CheckoutIntentId" = ci."Id"
                  AND cii."SellerId" = {0}
            )
              AND ci."CreatedAt" >= {1}
              AND ci."CreatedAt" < {2}
              AND ci."AttributionSource" IS NOT NULL
            ORDER BY ci."Id"
        ),
        intent_orders AS (
            SELECT
                si."Id",
                si.source,
                COALESCE(SUM(ol."PricePaid"), 0) AS seller_revenue,
                EXISTS (
                    SELECT 1
                    FROM orders o
                    WHERE o."CheckoutIntentId" = si."Id"
                ) AS has_order
            FROM seller_intents si
            LEFT JOIN orders o ON o."CheckoutIntentId" = si."Id"
            LEFT JOIN order_lines ol ON ol."OrderId" = o."Id" AND ol."SellerId" = {0}
            GROUP BY si."Id", si.source
        ),
        intent_traffic AS (
            SELECT
                source,
                COUNT(*)::int AS checkout_starts,
                COUNT(*) FILTER (WHERE has_order)::int AS completed_orders,
                COALESCE(SUM(seller_revenue) FILTER (WHERE has_order), 0) AS attributed_gross_revenue
            FROM intent_orders
            GROUP BY source
        ),
        sources AS (
            SELECT source FROM event_traffic
            UNION
            SELECT source FROM event_uv
            UNION
            SELECT source FROM intent_traffic
        )
        SELECT
            s.source AS "Source",
            COALESCE(et.product_views, 0) AS "ProductViews",
            COALESCE(eu.unique_visitors, 0) AS "UniqueVisitors",
            COALESCE(it.checkout_starts, 0) AS "CheckoutStarts",
            COALESCE(it.completed_orders, 0) AS "CompletedOrders",
            COALESCE(it.attributed_gross_revenue, 0) AS "AttributedGrossRevenue"
        FROM sources s
        LEFT JOIN event_traffic et ON et.source = s.source
        LEFT JOIN event_uv eu ON eu.source = s.source
        LEFT JOIN intent_traffic it ON it.source = s.source
        ORDER BY COALESCE(et.product_views, 0) DESC, s.source ASC
        """;

    internal const string EXTERNAL_REFERRERS = """
        WITH event_referrers AS (
            SELECT
                COALESCE(ae."ReferrerHost", '') AS referrer_host,
                COUNT(*)::bigint AS product_views,
                COUNT(DISTINCT ae."VisitorId")::bigint AS unique_visitors
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."OccurredAt" >= {1}
              AND ae."OccurredAt" < {2}
              AND ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
              AND ae."Source" = 'EXTERNAL'
            GROUP BY COALESCE(ae."ReferrerHost", '')
        ),
        seller_intents AS (
            SELECT DISTINCT ON (ci."Id")
                ci."Id",
                COALESCE(ci."AttributionReferrerHost", '') AS referrer_host
            FROM checkout_intents ci
            WHERE EXISTS (
                SELECT 1
                FROM checkout_intent_items cii
                WHERE cii."CheckoutIntentId" = ci."Id"
                  AND cii."SellerId" = {0}
            )
              AND ci."CreatedAt" >= {1}
              AND ci."CreatedAt" < {2}
              AND ci."AttributionSource" = 'EXTERNAL'
            ORDER BY ci."Id"
        ),
        intent_orders AS (
            SELECT
                si."Id",
                si.referrer_host,
                COALESCE(SUM(ol."PricePaid"), 0) AS seller_revenue,
                EXISTS (
                    SELECT 1
                    FROM orders o
                    WHERE o."CheckoutIntentId" = si."Id"
                ) AS has_order
            FROM seller_intents si
            LEFT JOIN orders o ON o."CheckoutIntentId" = si."Id"
            LEFT JOIN order_lines ol ON ol."OrderId" = o."Id" AND ol."SellerId" = {0}
            GROUP BY si."Id", si.referrer_host
        ),
        intent_referrers AS (
            SELECT
                referrer_host,
                COUNT(*)::int AS checkout_starts,
                COUNT(*) FILTER (WHERE has_order)::int AS completed_orders,
                COALESCE(SUM(seller_revenue) FILTER (WHERE has_order), 0) AS attributed_gross_revenue
            FROM intent_orders
            GROUP BY referrer_host
        ),
        referrers AS (
            SELECT referrer_host FROM event_referrers
            UNION
            SELECT referrer_host FROM intent_referrers
        )
        SELECT
            r.referrer_host AS "ReferrerHost",
            COALESCE(er.product_views, 0) AS "ProductViews",
            COALESCE(er.unique_visitors, 0) AS "UniqueVisitors",
            COALESCE(ir.checkout_starts, 0) AS "CheckoutStarts",
            COALESCE(ir.completed_orders, 0) AS "CompletedOrders",
            COALESCE(ir.attributed_gross_revenue, 0) AS "AttributedGrossRevenue"
        FROM referrers r
        LEFT JOIN event_referrers er ON er.referrer_host = r.referrer_host
        LEFT JOIN intent_referrers ir ON ir.referrer_host = r.referrer_host
        ORDER BY COALESCE(er.product_views, 0) DESC, r.referrer_host ASC
        LIMIT {3}
        """;
}
