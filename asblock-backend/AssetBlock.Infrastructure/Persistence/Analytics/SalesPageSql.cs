using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Infrastructure.Persistence.Analytics;

internal static class SalesPageSql
{
    public static string Build(AnalyticsProductTypeFilter productType, bool hasCursor)
    {
        var productFilter = productType switch
        {
            AnalyticsProductTypeFilter.ALL => "",
            AnalyticsProductTypeFilter.ASSET => "\n    AND o.\"AssetId\" IS NOT NULL",
            AnalyticsProductTypeFilter.BUNDLE => "\n    AND o.\"BundleId\" IS NOT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(productType), productType, null)
        };

        var cursorFilter = hasCursor
            ? """

                  AND (
                      o."PurchasedAt" < {3}
                      OR (o."PurchasedAt" = {3} AND o."Id" < {4})
                  )
              """
            : "";

        var limitParam = hasCursor ? "{5}" : "{3}";

        return
            """
            WITH page_orders AS (
                SELECT o."Id", o."AssetId", o."BundleId", o."ProductTitle", o."PurchasedAt"
                FROM orders o
                WHERE o."PurchasedAt" >= {1}
                  AND o."PurchasedAt" < {2}
                  AND EXISTS (
                      SELECT 1
                      FROM order_lines ol
                      WHERE ol."OrderId" = o."Id"
                        AND ol."SellerId" = {0}
                  )
            """ +
            productFilter +
            cursorFilter +
            """
                ORDER BY o."PurchasedAt" DESC, o."Id" DESC
                LIMIT 
            """ +
            limitParam +
            """
            ),
            line_stats AS (
                SELECT
                    ol."OrderId",
                    COUNT(*)::int AS "Units",
                    SUM(ol."PricePaid") AS "GrossRevenue"
                FROM order_lines ol
                INNER JOIN page_orders po ON po."Id" = ol."OrderId"
                WHERE ol."SellerId" = {0}
                GROUP BY ol."OrderId"
            )
            SELECT
                CASE WHEN po."BundleId" IS NOT NULL THEN 1 ELSE 0 END AS "ProductKind",
                COALESCE(po."BundleId", po."AssetId") AS "ProductId",
                po."ProductTitle",
                po."Id" AS "OrderId",
                po."PurchasedAt",
                ls."Units",
                ls."GrossRevenue"
            FROM page_orders po
            INNER JOIN line_stats ls ON ls."OrderId" = po."Id"
            ORDER BY po."PurchasedAt" DESC, po."Id" DESC
            """;
    }
}
