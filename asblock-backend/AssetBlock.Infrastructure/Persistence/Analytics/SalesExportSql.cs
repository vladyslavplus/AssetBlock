using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Infrastructure.Persistence.Analytics;

internal static class SalesExportSql
{
    private const string ORDER_SELLER_EXISTS =
        """
        EXISTS (
            SELECT 1
            FROM order_lines ol
            WHERE ol."OrderId" = o."Id"
              AND ol."SellerId" = {0}
        )
        """;

    private const string EXPORT_LINE_STATS =
        """
        line_stats AS (
            SELECT
                ol."OrderId",
                COUNT(*)::int AS "Units",
                SUM(ol."PricePaid") AS "GrossRevenue"
            FROM order_lines ol
            INNER JOIN export_orders eo ON eo."Id" = ol."OrderId"
            WHERE ol."SellerId" = {0}
            GROUP BY ol."OrderId"
        )
        """;

    private const string EXPORT_SELECT =
        """
        SELECT
            eo."PeekCount",
            CASE WHEN eo."BundleId" IS NOT NULL THEN 1 ELSE 0 END AS "ProductKind",
            COALESCE(eo."BundleId", eo."AssetId") AS "ProductId",
            eo."ProductTitle",
            eo."Id" AS "OrderId",
            eo."PurchasedAt",
            ls."Units",
            ls."GrossRevenue"
        FROM export_orders eo
        INNER JOIN line_stats ls ON ls."OrderId" = eo."Id"
        ORDER BY eo."PurchasedAt" DESC, eo."Id" DESC
        """;

    internal static string BuildExportQuery(AnalyticsProductTypeFilter productType)
    {
        var productFilter = BuildProductFilter(productType);

        return
            """
            WITH export_candidates AS (
                SELECT
                    o."Id",
                    o."AssetId",
                    o."BundleId",
                    o."ProductTitle",
                    o."PurchasedAt"
                FROM orders o
                WHERE o."PurchasedAt" >= {1}
                  AND o."PurchasedAt" < {2}
                  AND 
            """ +
            ORDER_SELLER_EXISTS +
            productFilter +
            """
                ORDER BY o."PurchasedAt" DESC, o."Id" DESC
                LIMIT {3}
            ),
            export_orders AS (
                SELECT
                    ec."Id",
                    ec."AssetId",
                    ec."BundleId",
                    ec."ProductTitle",
                    ec."PurchasedAt",
                    COUNT(*) OVER() AS "PeekCount"
                FROM export_candidates ec
            ),
            """ +
            EXPORT_LINE_STATS +
            """
            
            """ +
            EXPORT_SELECT;
    }

    private static string BuildProductFilter(AnalyticsProductTypeFilter productType) =>
        productType switch
        {
            AnalyticsProductTypeFilter.ALL => "",
            AnalyticsProductTypeFilter.ASSET => "\n                  AND o.\"AssetId\" IS NOT NULL",
            AnalyticsProductTypeFilter.BUNDLE => "\n                  AND o.\"BundleId\" IS NOT NULL",
            _ => throw new ArgumentOutOfRangeException(nameof(productType), productType, null)
        };
}
