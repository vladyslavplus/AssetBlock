namespace AssetBlock.Infrastructure.Persistence.Analytics;

/// <summary>PostgreSQL statements for daily engagement rollup recomputation.</summary>
internal static class AnalyticsDailyRollupSql
{
    internal const string UPSERT_SELLER_DAILY = """
        INSERT INTO seller_analytics_daily (
            "SellerId", "DayUtc", "AssetViews", "BundleViews", "CollectionViews",
            "CollectionItemClicks", "DownloadRequests", "UniqueVisitors", "UpdatedAt")
        SELECT
            "SellerId",
            {2}::date,
            COUNT(*) FILTER (WHERE "EventType" = 'ASSET_VIEW'),
            COUNT(*) FILTER (WHERE "EventType" = 'BUNDLE_VIEW'),
            COUNT(*) FILTER (WHERE "EventType" = 'COLLECTION_VIEW'),
            COUNT(*) FILTER (WHERE "EventType" = 'COLLECTION_ITEM_CLICK'),
            COUNT(*) FILTER (WHERE "EventType" = 'DOWNLOAD_REQUESTED'),
            COUNT(DISTINCT "VisitorId") FILTER (
                WHERE "EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
            ),
            {3}
        FROM analytics_events
        WHERE "OccurredAt" >= {0} AND "OccurredAt" < {1}
        GROUP BY "SellerId"
        ON CONFLICT ("SellerId", "DayUtc") DO UPDATE SET
            "AssetViews" = EXCLUDED."AssetViews",
            "BundleViews" = EXCLUDED."BundleViews",
            "CollectionViews" = EXCLUDED."CollectionViews",
            "CollectionItemClicks" = EXCLUDED."CollectionItemClicks",
            "DownloadRequests" = EXCLUDED."DownloadRequests",
            "UniqueVisitors" = EXCLUDED."UniqueVisitors",
            "UpdatedAt" = EXCLUDED."UpdatedAt"
        """;

    internal const string DELETE_STALE_SELLER_DAILY = """
        DELETE FROM seller_analytics_daily sad
        WHERE sad."DayUtc" = {2}::date
          AND NOT EXISTS (
              SELECT 1
              FROM analytics_events ae
              WHERE ae."SellerId" = sad."SellerId"
                AND ae."OccurredAt" >= {0}
                AND ae."OccurredAt" < {1}
          )
        """;

    internal const string UPSERT_PRODUCT_DAILY = """
        INSERT INTO product_analytics_daily (
            "SellerId", "DayUtc", "ProductType", "ProductId",
            "Views", "DownloadRequests", "UniqueVisitors", "UpdatedAt")
        SELECT
            "SellerId",
            {2}::date,
            "ProductType",
            "ProductId",
            "Views",
            "DownloadRequests",
            "UniqueVisitors",
            {3}
        FROM (
            SELECT
                "SellerId",
                'ASSET'::varchar AS "ProductType",
                "AssetId" AS "ProductId",
                COUNT(*) FILTER (WHERE "EventType" = 'ASSET_VIEW') AS "Views",
                COUNT(*) FILTER (WHERE "EventType" = 'DOWNLOAD_REQUESTED') AS "DownloadRequests",
                COUNT(DISTINCT "VisitorId") FILTER (WHERE "EventType" = 'ASSET_VIEW') AS "UniqueVisitors"
            FROM analytics_events
            WHERE "OccurredAt" >= {0}
              AND "OccurredAt" < {1}
              AND "AssetId" IS NOT NULL
              AND "EventType" IN ('ASSET_VIEW', 'DOWNLOAD_REQUESTED')
            GROUP BY "SellerId", "AssetId"
            UNION ALL
            SELECT
                "SellerId",
                'BUNDLE'::varchar AS "ProductType",
                "BundleId" AS "ProductId",
                COUNT(*) FILTER (WHERE "EventType" = 'BUNDLE_VIEW') AS "Views",
                0::bigint AS "DownloadRequests",
                COUNT(DISTINCT "VisitorId") FILTER (WHERE "EventType" = 'BUNDLE_VIEW') AS "UniqueVisitors"
            FROM analytics_events
            WHERE "OccurredAt" >= {0}
              AND "OccurredAt" < {1}
              AND "BundleId" IS NOT NULL
              AND "EventType" = 'BUNDLE_VIEW'
            GROUP BY "SellerId", "BundleId"
        ) product_daily
        ON CONFLICT ("SellerId", "DayUtc", "ProductType", "ProductId") DO UPDATE SET
            "Views" = EXCLUDED."Views",
            "DownloadRequests" = EXCLUDED."DownloadRequests",
            "UniqueVisitors" = EXCLUDED."UniqueVisitors",
            "UpdatedAt" = EXCLUDED."UpdatedAt"
        """;

    internal const string DELETE_STALE_PRODUCT_DAILY = """
        DELETE FROM product_analytics_daily pad
        WHERE pad."DayUtc" = {2}::date
          AND NOT EXISTS (
              SELECT 1
              FROM analytics_events ae
              WHERE ae."SellerId" = pad."SellerId"
                AND ae."OccurredAt" >= {0}
                AND ae."OccurredAt" < {1}
                AND (
                    (pad."ProductType" = 'ASSET'
                        AND ae."AssetId" = pad."ProductId"
                        AND ae."EventType" IN ('ASSET_VIEW', 'DOWNLOAD_REQUESTED'))
                    OR (pad."ProductType" = 'BUNDLE'
                        AND ae."BundleId" = pad."ProductId"
                        AND ae."EventType" = 'BUNDLE_VIEW')
                )
          )
        """;

    internal const string UPSERT_COLLECTION_DAILY = """
        INSERT INTO collection_analytics_daily (
            "SellerId", "DayUtc", "CollectionId", "Views", "ItemClicks", "UniqueVisitors", "UpdatedAt")
        SELECT
            "SellerId",
            {2}::date,
            "CollectionId",
            COUNT(*) FILTER (WHERE "EventType" = 'COLLECTION_VIEW'),
            COUNT(*) FILTER (WHERE "EventType" = 'COLLECTION_ITEM_CLICK'),
            COUNT(DISTINCT "VisitorId") FILTER (WHERE "EventType" = 'COLLECTION_VIEW'),
            {3}
        FROM analytics_events
        WHERE "OccurredAt" >= {0}
          AND "OccurredAt" < {1}
          AND "CollectionId" IS NOT NULL
          AND "EventType" IN ('COLLECTION_VIEW', 'COLLECTION_ITEM_CLICK')
        GROUP BY "SellerId", "CollectionId"
        ON CONFLICT ("SellerId", "DayUtc", "CollectionId") DO UPDATE SET
            "Views" = EXCLUDED."Views",
            "ItemClicks" = EXCLUDED."ItemClicks",
            "UniqueVisitors" = EXCLUDED."UniqueVisitors",
            "UpdatedAt" = EXCLUDED."UpdatedAt"
        """;

    internal const string DELETE_STALE_COLLECTION_DAILY = """
        DELETE FROM collection_analytics_daily cad
        WHERE cad."DayUtc" = {2}::date
          AND NOT EXISTS (
              SELECT 1
              FROM analytics_events ae
              WHERE ae."SellerId" = cad."SellerId"
                AND ae."OccurredAt" >= {0}
                AND ae."OccurredAt" < {1}
                AND ae."CollectionId" = cad."CollectionId"
                AND ae."EventType" IN ('COLLECTION_VIEW', 'COLLECTION_ITEM_CLICK')
          )
        """;

    internal const string UPSERT_TRAFFIC_DAILY = """
        INSERT INTO traffic_analytics_daily (
            "SellerId", "DayUtc", "Source", "ReferrerHostKey", "ProductViews", "UniqueVisitors", "UpdatedAt")
        SELECT
            "SellerId",
            {2}::date,
            "Source",
            CASE
                WHEN "Source" = 'EXTERNAL' THEN COALESCE("ReferrerHost", '')
                ELSE ''
            END AS "ReferrerHostKey",
            COUNT(*) AS "ProductViews",
            COUNT(DISTINCT "VisitorId") AS "UniqueVisitors",
            {3}
        FROM analytics_events
        WHERE "OccurredAt" >= {0}
          AND "OccurredAt" < {1}
          AND "EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
        GROUP BY
            "SellerId",
            "Source",
            CASE
                WHEN "Source" = 'EXTERNAL' THEN COALESCE("ReferrerHost", '')
                ELSE ''
            END
        ON CONFLICT ("SellerId", "DayUtc", "Source", "ReferrerHostKey") DO UPDATE SET
            "ProductViews" = EXCLUDED."ProductViews",
            "UniqueVisitors" = EXCLUDED."UniqueVisitors",
            "UpdatedAt" = EXCLUDED."UpdatedAt"
        """;

    internal const string DELETE_STALE_TRAFFIC_DAILY = """
        DELETE FROM traffic_analytics_daily tad
        WHERE tad."DayUtc" = {2}::date
          AND NOT EXISTS (
              SELECT 1
              FROM analytics_events ae
              WHERE ae."SellerId" = tad."SellerId"
                AND ae."OccurredAt" >= {0}
                AND ae."OccurredAt" < {1}
                AND ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
                AND ae."Source" = tad."Source"
                AND (
                    CASE
                        WHEN ae."Source" = 'EXTERNAL' THEN COALESCE(ae."ReferrerHost", '')
                        ELSE ''
                    END
                ) = tad."ReferrerHostKey"
          )
        """;

    internal const string DELETE_EXPIRED_EVENTS_BATCH = """
        DELETE FROM analytics_events ae
        WHERE ae."Id" IN (
            SELECT e."Id"
            FROM analytics_events e
            WHERE e."OccurredAt" < {0}
            ORDER BY e."OccurredAt", e."Id"
            LIMIT {1}
        )
        """;
}
