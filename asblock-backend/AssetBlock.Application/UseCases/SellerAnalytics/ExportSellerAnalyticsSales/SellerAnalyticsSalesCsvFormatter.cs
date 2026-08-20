using System.Globalization;
using System.Text;
using AssetBlock.Domain.Core.Dto.Analytics;

namespace AssetBlock.Application.UseCases.SellerAnalytics.ExportSellerAnalyticsSales;

internal static class SellerAnalyticsSalesCsvFormatter
{
    internal static byte[] Utf8Bom => [0xEF, 0xBB, 0xBF];

    internal const string HEADER =
        "purchased_at_utc,order_id,product_type,product_id,product_title,units,gross_revenue_usd";

    internal static string FormatRow(AnalyticsSalesExportRow row)
    {
        var builder = new StringBuilder(256);
        AppendField(builder, FormatPurchasedAtUtc(row.PurchasedAt));
        builder.Append(',');
        AppendField(builder, row.OrderId.ToString());
        builder.Append(',');
        AppendField(builder, row.ProductType);
        builder.Append(',');
        AppendField(builder, row.ProductId.ToString());
        builder.Append(',');
        AppendField(builder, row.ProductTitle);
        builder.Append(',');
        AppendField(builder, row.Units.ToString(CultureInfo.InvariantCulture));
        builder.Append(',');
        AppendField(builder, row.GrossRevenue.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    internal static string FormatPurchasedAtUtc(DateTimeOffset purchasedAt) =>
        purchasedAt.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss.fffffff'Z'", CultureInfo.InvariantCulture);

    internal static string EscapeField(string value)
    {
        var mustQuote = value.Contains(',')
            || value.Contains('"')
            || value.Contains('\r')
            || value.Contains('\n');

        if (!mustQuote)
        {
            return value;
        }

        return "\"" + value.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    internal static string SanitizeFormulaInjection(string value)
    {
        var index = 0;
        while (index < value.Length && char.IsWhiteSpace(value[index]))
        {
            index++;
        }

        if (index < value.Length && value[index] is '=' or '+' or '-' or '@')
        {
            return "'" + value;
        }

        return value;
    }

    private static void AppendField(StringBuilder builder, string value)
    {
        builder.Append(EscapeField(SanitizeFormulaInjection(value)));
    }
}
