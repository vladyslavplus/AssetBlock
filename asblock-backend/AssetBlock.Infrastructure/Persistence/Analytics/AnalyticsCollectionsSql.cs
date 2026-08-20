using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Infrastructure.Persistence.Analytics;

internal static class AnalyticsCollectionsSql
{
    internal static string BuildCollectionsPageSql(string orderBy) =>
        """
        WITH coverage AS (
            SELECT MIN(ae."OccurredAt") AS available_from
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
        ),
        collection_universe AS (
            SELECT
                c."Id" AS "CollectionId",
                c."Title" AS "Title",
                c."Status" AS "Status",
                c."UpdatedAt" AS "UpdatedAt"
            FROM collections c
            WHERE c."SellerId" = {0}
        ),
        engagement AS (
            SELECT
                combined."CollectionId",
                SUM(combined.views)::bigint AS views,
                SUM(combined.item_clicks)::bigint AS item_clicks
            FROM (
                SELECT
                    cad."CollectionId",
                    SUM(cad."Views") AS views,
                    SUM(cad."ItemClicks") AS item_clicks
                FROM collection_analytics_daily cad
                WHERE cad."SellerId" = {0}
                  AND cad."DayUtc" >= ({1} AT TIME ZONE 'UTC')::date
                  AND cad."DayUtc" < ({2} AT TIME ZONE 'UTC')::date
                  AND cad."DayUtc" < (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
                GROUP BY cad."CollectionId"
                UNION ALL
                SELECT
                    ae."CollectionId",
                    COUNT(*) FILTER (WHERE ae."EventType" = 'COLLECTION_VIEW')::bigint AS views,
                    COUNT(*) FILTER (WHERE ae."EventType" = 'COLLECTION_ITEM_CLICK')::bigint AS item_clicks
                FROM analytics_events ae
                WHERE ae."SellerId" = {0}
                  AND ae."CollectionId" IS NOT NULL
                  AND ae."OccurredAt" >= {1}
                  AND ae."OccurredAt" < {2}
                  AND ae."EventType" IN ('COLLECTION_VIEW', 'COLLECTION_ITEM_CLICK')
                  AND (
                      (ae."OccurredAt" AT TIME ZONE 'UTC')::date = (CURRENT_TIMESTAMP AT TIME ZONE 'UTC')::date
                      OR NOT EXISTS (
                          SELECT 1
                          FROM collection_analytics_daily cad
                          WHERE cad."SellerId" = ae."SellerId"
                            AND cad."CollectionId" = ae."CollectionId"
                            AND cad."DayUtc" = (ae."OccurredAt" AT TIME ZONE 'UTC')::date
                      )
                  )
                GROUP BY ae."CollectionId"
            ) combined
            GROUP BY combined."CollectionId"
        ),
        recent_engagement AS (
            SELECT
                ae."CollectionId",
                MAX(ae."OccurredAt") AS max_occurred
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."CollectionId" IS NOT NULL
              AND ae."OccurredAt" >= {1}
              AND ae."OccurredAt" < {2}
              AND ae."EventType" IN ('COLLECTION_VIEW', 'COLLECTION_ITEM_CLICK')
            GROUP BY ae."CollectionId"
        ),
        engagement_uv AS (
            SELECT
                ae."CollectionId",
                COUNT(DISTINCT ae."VisitorId")::bigint AS unique_visitors
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."CollectionId" IS NOT NULL
              AND ae."OccurredAt" >= {1}
              AND ae."OccurredAt" < {2}
              AND ae."EventType" = 'COLLECTION_VIEW'
            GROUP BY ae."CollectionId"
        ),
        attribution AS (
            WITH seller_intents AS (
                SELECT DISTINCT ON (ci."Id")
                    ci."Id",
                    ci."AttributionCollectionId" AS collection_id
                FROM checkout_intents ci
                WHERE EXISTS (
                    SELECT 1
                    FROM checkout_intent_items cii
                    WHERE cii."CheckoutIntentId" = ci."Id"
                      AND cii."SellerId" = {0}
                )
                  AND ci."AttributionCollectionId" IS NOT NULL
                  AND ci."AttributionSource" = 'COLLECTION'
                  AND ci."CreatedAt" >= {1}
                  AND ci."CreatedAt" < {2}
                ORDER BY ci."Id"
            ),
            intent_orders AS (
                SELECT
                    si."Id",
                    si.collection_id,
                    COALESCE(SUM(ol."PricePaid"), 0) AS seller_revenue,
                    EXISTS (
                        SELECT 1
                        FROM orders o
                        WHERE o."CheckoutIntentId" = si."Id"
                    ) AS has_order
                FROM seller_intents si
                LEFT JOIN orders o ON o."CheckoutIntentId" = si."Id"
                LEFT JOIN order_lines ol ON ol."OrderId" = o."Id" AND ol."SellerId" = {0}
                GROUP BY si."Id", si.collection_id
            )
            SELECT
                collection_id,
                COUNT(*)::int AS attributed_checkout_starts,
                COUNT(*) FILTER (WHERE has_order)::int AS attributed_completed_orders,
                COALESCE(SUM(seller_revenue) FILTER (WHERE has_order), 0) AS attributed_gross_revenue
            FROM intent_orders
            GROUP BY collection_id
        ),
        rows AS (
            SELECT
                u."CollectionId",
                u."Title",
                u."Status",
                COALESCE(e.views, 0) AS views,
                COALESCE(uv.unique_visitors, 0) AS unique_visitors,
                COALESCE(e.item_clicks, 0) AS item_clicks,
                COALESCE(a.attributed_checkout_starts, 0) AS attributed_checkout_starts,
                COALESCE(a.attributed_completed_orders, 0) AS attributed_completed_orders,
                COALESCE(a.attributed_gross_revenue, 0) AS attributed_gross_revenue,
                COALESCE(re.max_occurred, u."UpdatedAt") AS recent_at,
                COUNT(*) OVER()::int AS "TotalCount"
            FROM collection_universe u
            LEFT JOIN engagement e ON e."CollectionId" = u."CollectionId"
            LEFT JOIN engagement_uv uv ON uv."CollectionId" = u."CollectionId"
            LEFT JOIN attribution a ON a.collection_id = u."CollectionId"
            LEFT JOIN recent_engagement re ON re."CollectionId" = u."CollectionId"
        )
        SELECT
            "CollectionId",
            "Title",
            "Status",
            views AS "Views",
            unique_visitors AS "UniqueVisitors",
            item_clicks AS "ItemClicks",
            attributed_checkout_starts AS "AttributedCheckoutStarts",
            attributed_completed_orders AS "AttributedCompletedOrders",
            attributed_gross_revenue AS "AttributedGrossRevenue",
            recent_at AS "RecentAt",
            "TotalCount"
        FROM rows
        ORDER BY
        """ + "\n" + orderBy + "\nOFFSET {3} LIMIT {4}";

    internal const string TOP_CLICKED_ASSETS_FOR_COLLECTIONS = """
        WITH ranked AS (
            SELECT
                ae."CollectionId",
                ae."AssetId",
                COUNT(*)::bigint AS clicks,
                ROW_NUMBER() OVER (
                    PARTITION BY ae."CollectionId"
                    ORDER BY COUNT(*) DESC, ae."AssetId" ASC
                ) AS rn
            FROM analytics_events ae
            WHERE ae."SellerId" = {0}
              AND ae."CollectionId" = ANY({1})
              AND ae."AssetId" IS NOT NULL
              AND ae."EventType" = 'COLLECTION_ITEM_CLICK'
              AND ae."OccurredAt" >= {2}
              AND ae."OccurredAt" < {3}
            GROUP BY ae."CollectionId", ae."AssetId"
        )
        SELECT
            r."CollectionId",
            r."AssetId",
            COALESCE(a."Title", r."AssetId"::text) AS "Title",
            r.clicks AS "Clicks"
        FROM ranked r
        LEFT JOIN assets a ON a."Id" = r."AssetId"
        WHERE r.rn <= {4}
        ORDER BY r."CollectionId", r.rn
        """;

    // Full coverage: requested engagement sort. Incomplete coverage + VIEWS/CLICKS:
    // fall back to attributed revenue DESC (hidden engagement values must not decide page order).
    private const string FULL_ENGAGEMENT_COVERAGE_SQL = """
        ((SELECT available_from FROM coverage) IS NOT NULL AND {1} >= (SELECT available_from FROM coverage))
        """;

    internal static string BuildOrderBy(AnalyticsCollectionSort sort, AnalyticsSortDirection direction)
    {
        return (sort, direction) switch
        {
            (AnalyticsCollectionSort.VIEWS, AnalyticsSortDirection.ASC) =>
                BuildCoverageAwareEngagementOrderBy("views", "ASC"),
            (AnalyticsCollectionSort.VIEWS, AnalyticsSortDirection.DESC) =>
                BuildCoverageAwareEngagementOrderBy("views", "DESC"),
            (AnalyticsCollectionSort.CLICKS, AnalyticsSortDirection.ASC) =>
                BuildCoverageAwareEngagementOrderBy("item_clicks", "ASC"),
            (AnalyticsCollectionSort.CLICKS, AnalyticsSortDirection.DESC) =>
                BuildCoverageAwareEngagementOrderBy("item_clicks", "DESC"),
            (AnalyticsCollectionSort.ATTRIBUTED_REVENUE, AnalyticsSortDirection.ASC) =>
                """ attributed_gross_revenue ASC, "CollectionId" ASC """,
            (AnalyticsCollectionSort.ATTRIBUTED_REVENUE, AnalyticsSortDirection.DESC) =>
                """ attributed_gross_revenue DESC, "CollectionId" ASC """,
            (AnalyticsCollectionSort.RECENT, AnalyticsSortDirection.ASC) =>
                """ recent_at ASC NULLS LAST, "CollectionId" ASC """,
            (AnalyticsCollectionSort.RECENT, AnalyticsSortDirection.DESC) =>
                """ recent_at DESC NULLS LAST, "CollectionId" ASC """,
            _ => throw new ArgumentOutOfRangeException(
                nameof(direction),
                direction,
                $"Unsupported analytics collection sort combination: {sort}/{direction}.")
        };
    }

    private static string BuildCoverageAwareEngagementOrderBy(
        string engagementColumn,
        string engagementDirection) =>
        $"""
            CASE WHEN {FULL_ENGAGEMENT_COVERAGE_SQL} THEN {engagementColumn} END {engagementDirection} NULLS LAST,
            CASE WHEN NOT {FULL_ENGAGEMENT_COVERAGE_SQL} THEN attributed_gross_revenue END DESC NULLS LAST,
            "CollectionId" ASC
            """;
}
