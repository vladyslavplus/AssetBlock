using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.Application.UseCases.SellerAnalytics.GetSellerAnalyticsSales;

/// <summary>
/// Encodes/decodes the opaque keyset cursor for the sales pagination feed.
/// Format: versioned JSON (v1) → base64url without padding.
/// </summary>
internal static class SalesCursorCodec
{
    private const string VERSION = "v1";

    private sealed record CursorPayload(string V, long PurchasedAtTicks, string OrderId);

    public static string Encode(DateTimeOffset purchasedAt, Guid orderId)
    {
        var payload = new CursorPayload(VERSION, purchasedAt.UtcTicks, orderId.ToString("N"));
        var json = JsonSerializer.Serialize(payload);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    }

    public static bool TryDecode(string cursor, out DateTimeOffset purchasedAt, out Guid orderId)
    {
        purchasedAt = default;
        orderId = Guid.Empty;

        if (cursor.Length > AnalyticsConstants.MAX_CURSOR_LENGTH)
        {
            return false;
        }

        try
        {
            var padded = cursor.Replace('-', '+').Replace('_', '/');
            var remainder = padded.Length % 4;
            if (remainder != 0)
            {
                padded += new string('=', 4 - remainder);
            }

            var bytes = Convert.FromBase64String(padded);
            var json = Encoding.UTF8.GetString(bytes);
            var payload = JsonSerializer.Deserialize<CursorPayload>(json);

            if (payload is null || payload.V != VERSION)
            {
                return false;
            }

            if (!Guid.TryParseExact(payload.OrderId, "N", out orderId))
            {
                return false;
            }

            purchasedAt = new DateTimeOffset(payload.PurchasedAtTicks, TimeSpan.Zero);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
