using System.Security.Cryptography;
using System.Text;

namespace AssetBlock.Infrastructure.RateLimiting;

internal static class AnalyticsRateLimitPartitionHasher
{
    internal static string HashPartition(string domain, string partitionMaterial, string secret)
    {
        var payload = $"assetblock:analytics:ratelimit:{domain}\n{partitionMaterial}";
        var keyBytes = Encoding.UTF8.GetBytes(secret);
        var payloadBytes = Encoding.UTF8.GetBytes(payload);
        var hash = HMACSHA256.HashData(keyBytes, payloadBytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
