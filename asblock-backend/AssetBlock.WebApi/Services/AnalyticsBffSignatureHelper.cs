using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace AssetBlock.WebApi.Services;

internal static partial class AnalyticsBffSignatureHelper
{
    private const string PARTITION_PREFIX = "assetblock:analytics:partition:v1\n";
    private const string REQUEST_PREFIX = "assetblock:analytics:request:v1\nPOST\n/api/analytics/events\n";
    private const int TIMESTAMP_TOLERANCE_SECONDS = 120;
    private const int HEX_LENGTH = 64;

    [GeneratedRegex("^[0-9a-f]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex LowerHex64Regex();

    internal static string CreatePartition(string normalizedIp, string secret) =>
        ComputeHmacHex(secret, PARTITION_PREFIX + normalizedIp);

    internal static string CreateRequestSignature(string timestamp, string partition, string secret) =>
        ComputeHmacHex(secret, REQUEST_PREFIX + timestamp + "\n" + partition);

    internal static bool IsLowerHex64(string? value) =>
        value is { Length: HEX_LENGTH } && LowerHex64Regex().IsMatch(value);

    internal static bool TryParseUnixTimestampSeconds(string? value, out long seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(value) || !long.TryParse(value.Trim(), out seconds))
        {
            return false;
        }

        return seconds >= 0;
    }

    internal static bool IsTimestampWithinTolerance(long timestampSeconds, DateTimeOffset utcNow) =>
        Math.Abs(utcNow.ToUnixTimeSeconds() - timestampSeconds) <= TIMESTAMP_TOLERANCE_SECONDS;

    internal static bool FixedTimeEqualsHex(string expectedHex, string actualHex)
    {
        var expectedBytes = Encoding.UTF8.GetBytes(expectedHex);
        var actualBytes = Encoding.UTF8.GetBytes(actualHex);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static string ComputeHmacHex(string secret, string payload)
    {
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
