namespace AssetBlock.Infrastructure.Persistence.Analytics;

/// <summary>Combined overview queries to reduce PostgreSQL round-trips.</summary>
internal static class AnalyticsOverviewBatchSql
{
    internal const string COMMERCE_CONTEXT = """
        WITH seller_intents AS (
            SELECT DISTINCT ON (ci."Id")
                ci."Id",
                ci."StripeSessionId",
                ci."Status",
                ci."AnalyticsSessionId"
            FROM checkout_intents ci
            INNER JOIN checkout_intent_items cii ON cii."CheckoutIntentId" = ci."Id"
            WHERE cii."SellerId" = {0}
              AND ci."CreatedAt" >= {1}
              AND ci."CreatedAt" < {2}
            ORDER BY ci."Id"
        ),
        commerce_funnel AS (
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
        ),
        tracked_coverage AS (
            SELECT
                CASE
                    WHEN COUNT(*) = 0 THEN NULL::numeric
                    ELSE COUNT(*) FILTER (WHERE "AnalyticsSessionId" IS NOT NULL)::numeric / COUNT(*)::numeric
                END AS "TrackedCheckoutCoverage"
            FROM seller_intents
        )
        SELECT
            (
                SELECT MIN(ae."OccurredAt")
                FROM analytics_events ae
                WHERE ae."SellerId" = {0}
            ) AS "EngagementAvailableFrom",
            cf."CheckoutStarts",
            cf."StripeSessionsAttached",
            cf."CompletedOrders",
            cf."CancelledCheckouts",
            cf."PendingCheckouts",
            tc."TrackedCheckoutCoverage"
        FROM commerce_funnel cf
        CROSS JOIN tracked_coverage tc
        """;

    internal const string ENGAGEMENT_METRICS_DUAL = """
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
        ),
        view_sessions AS (
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
            ca."ProductViews" AS "CurrentProductViews",
            ca."UniqueVisitors" AS "CurrentUniqueVisitors",
            ca."DownloadRequests" AS "CurrentDownloadRequests",
            ca."CollectionViews" AS "CurrentCollectionViews",
            ca."CollectionItemClicks" AS "CurrentCollectionItemClicks",
            coa."ProductViews" AS "ComparisonProductViews",
            coa."UniqueVisitors" AS "ComparisonUniqueVisitors",
            coa."DownloadRequests" AS "ComparisonDownloadRequests",
            coa."CollectionViews" AS "ComparisonCollectionViews",
            coa."CollectionItemClicks" AS "ComparisonCollectionItemClicks",
            (SELECT COUNT(*)::int FROM view_sessions) AS "ViewSessions",
            (SELECT COUNT(*)::int FROM checkout_sessions) AS "CheckoutSessions",
            (SELECT COUNT(*)::int FROM completed_sessions) AS "CompletedSessions"
        FROM current_agg ca
        CROSS JOIN comparison_agg coa
        """;

    internal const string ENGAGEMENT_METRICS_CURRENT = """
        WITH engagement_facts AS (
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
        ),
        view_sessions AS (
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
            ef."ProductViews" AS "CurrentProductViews",
            ef."UniqueVisitors" AS "CurrentUniqueVisitors",
            ef."DownloadRequests" AS "CurrentDownloadRequests",
            ef."CollectionViews" AS "CurrentCollectionViews",
            ef."CollectionItemClicks" AS "CurrentCollectionItemClicks",
            (SELECT COUNT(*)::int FROM view_sessions) AS "ViewSessions",
            (SELECT COUNT(*)::int FROM checkout_sessions) AS "CheckoutSessions",
            (SELECT COUNT(*)::int FROM completed_sessions) AS "CompletedSessions"
        FROM engagement_facts ef
        """;

    internal const string ENGAGEMENT_SERIES_COMBINED_DAY = """
        WITH event_series AS (
        """ + AnalyticsOverviewEngagementSql.ENGAGEMENT_EVENT_DAY_SERIES + """
        ),
        checkout_series AS (
        """ + AnalyticsOverviewEngagementSql.ENGAGEMENT_CHECKOUT_DAY_SERIES + """
        ),
        buckets AS (
            SELECT "DayUtc" FROM event_series
            UNION
            SELECT "DayUtc" FROM checkout_series
        )
        SELECT
            b."DayUtc",
            COALESCE(e."ProductViews", 0) AS "ProductViews",
            COALESCE(e."UniqueVisitors", 0) AS "UniqueVisitors",
            COALESCE(c."CheckoutStarts", 0) AS "CheckoutStarts",
            COALESCE(c."CompletedOrders", 0) AS "CompletedOrders",
            COALESCE(e."DownloadRequests", 0) AS "DownloadRequests"
        FROM buckets b
        LEFT JOIN event_series e ON e."DayUtc" = b."DayUtc"
        LEFT JOIN checkout_series c ON c."DayUtc" = b."DayUtc"
        ORDER BY b."DayUtc"
        """;

    internal const string ENGAGEMENT_SERIES_COMBINED_WEEK = """
        WITH event_series AS (
        """ + AnalyticsOverviewEngagementSql.ENGAGEMENT_EVENT_WEEK_SERIES + """
        ),
        checkout_series AS (
        """ + AnalyticsOverviewEngagementSql.ENGAGEMENT_CHECKOUT_WEEK_SERIES + """
        ),
        buckets AS (
            SELECT "DayUtc" FROM event_series
            UNION
            SELECT "DayUtc" FROM checkout_series
        )
        SELECT
            b."DayUtc",
            COALESCE(e."ProductViews", 0) AS "ProductViews",
            COALESCE(e."UniqueVisitors", 0) AS "UniqueVisitors",
            COALESCE(c."CheckoutStarts", 0) AS "CheckoutStarts",
            COALESCE(c."CompletedOrders", 0) AS "CompletedOrders",
            COALESCE(e."DownloadRequests", 0) AS "DownloadRequests"
        FROM buckets b
        LEFT JOIN event_series e ON e."DayUtc" = b."DayUtc"
        LEFT JOIN checkout_series c ON c."DayUtc" = b."DayUtc"
        ORDER BY b."DayUtc"
        """;

    internal const string ENGAGEMENT_SERIES_COMBINED_MONTH = """
        WITH event_series AS (
        """ + AnalyticsOverviewEngagementSql.ENGAGEMENT_EVENT_MONTH_SERIES + """
        ),
        checkout_series AS (
        """ + AnalyticsOverviewEngagementSql.ENGAGEMENT_CHECKOUT_MONTH_SERIES + """
        ),
        buckets AS (
            SELECT "DayUtc" FROM event_series
            UNION
            SELECT "DayUtc" FROM checkout_series
        )
        SELECT
            b."DayUtc",
            COALESCE(e."ProductViews", 0) AS "ProductViews",
            COALESCE(e."UniqueVisitors", 0) AS "UniqueVisitors",
            COALESCE(c."CheckoutStarts", 0) AS "CheckoutStarts",
            COALESCE(c."CompletedOrders", 0) AS "CompletedOrders",
            COALESCE(e."DownloadRequests", 0) AS "DownloadRequests"
        FROM buckets b
        LEFT JOIN event_series e ON e."DayUtc" = b."DayUtc"
        LEFT JOIN checkout_series c ON c."DayUtc" = b."DayUtc"
        ORDER BY b."DayUtc"
        """;

    internal const string TRAFFIC_UNION = """
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
                ci."AttributionSource"::text AS source,
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
            ORDER BY ci."Id"
        ),
        intent_orders AS (
            SELECT
                si."Id",
                si.source,
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
            GROUP BY si."Id", si.source, si.referrer_host
        ),
        intent_traffic AS (
            SELECT
                source,
                COUNT(*)::int AS checkout_starts,
                COUNT(*) FILTER (WHERE has_order)::int AS completed_orders,
                COALESCE(SUM(seller_revenue) FILTER (WHERE has_order), 0) AS attributed_gross_revenue
            FROM intent_orders
            WHERE source IS NOT NULL
            GROUP BY source
        ),
        sources AS (
            SELECT source FROM event_traffic
            UNION
            SELECT source FROM event_uv
            UNION
            SELECT source FROM intent_traffic
        ),
        source_rows AS (
            SELECT
                'SOURCE' AS "RowKind",
                s.source AS "Key",
                COALESCE(et.product_views, 0) AS "ProductViews",
                COALESCE(eu.unique_visitors, 0) AS "UniqueVisitors",
                COALESCE(it.checkout_starts, 0) AS "CheckoutStarts",
                COALESCE(it.completed_orders, 0) AS "CompletedOrders",
                COALESCE(it.attributed_gross_revenue, 0) AS "AttributedGrossRevenue",
                COALESCE(et.product_views, 0) AS sort_views
            FROM sources s
            LEFT JOIN event_traffic et ON et.source = s.source
            LEFT JOIN event_uv eu ON eu.source = s.source
            LEFT JOIN intent_traffic it ON it.source = s.source
        ),
        event_referrers AS (
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
        intent_referrers AS (
            SELECT
                referrer_host,
                COUNT(*)::int AS checkout_starts,
                COUNT(*) FILTER (WHERE has_order)::int AS completed_orders,
                COALESCE(SUM(seller_revenue) FILTER (WHERE has_order), 0) AS attributed_gross_revenue
            FROM intent_orders
            WHERE source = 'EXTERNAL'
            GROUP BY referrer_host
        ),
        referrers AS (
            SELECT referrer_host FROM event_referrers
            UNION
            SELECT referrer_host FROM intent_referrers
        ),
        referrer_rows AS (
            SELECT
                'REFERRER' AS "RowKind",
                r.referrer_host AS "Key",
                COALESCE(er.product_views, 0) AS "ProductViews",
                COALESCE(er.unique_visitors, 0) AS "UniqueVisitors",
                COALESCE(ir.checkout_starts, 0) AS "CheckoutStarts",
                COALESCE(ir.completed_orders, 0) AS "CompletedOrders",
                COALESCE(ir.attributed_gross_revenue, 0) AS "AttributedGrossRevenue",
                COALESCE(er.product_views, 0) AS sort_views
            FROM referrers r
            LEFT JOIN event_referrers er ON er.referrer_host = r.referrer_host
            LEFT JOIN intent_referrers ir ON ir.referrer_host = r.referrer_host
            ORDER BY sort_views DESC, r.referrer_host ASC
            LIMIT {3}
        )
        SELECT
            "RowKind",
            "Key",
            "ProductViews",
            "UniqueVisitors",
            "CheckoutStarts",
            "CompletedOrders",
            "AttributedGrossRevenue"
        FROM (
            SELECT
                "RowKind",
                "Key",
                "ProductViews",
                "UniqueVisitors",
                "CheckoutStarts",
                "CompletedOrders",
                "AttributedGrossRevenue",
                sort_views,
                "Key" AS tie_key
            FROM source_rows
            UNION ALL
            SELECT
                "RowKind",
                "Key",
                "ProductViews",
                "UniqueVisitors",
                "CheckoutStarts",
                "CompletedOrders",
                "AttributedGrossRevenue",
                sort_views,
                "Key" AS tie_key
            FROM referrer_rows
        ) traffic_union
        ORDER BY
            CASE WHEN "RowKind" = 'SOURCE' THEN 0 ELSE 1 END,
            sort_views DESC,
            tie_key ASC
        """;
}
