using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Services;

internal sealed class RecommendationExposureSigner(IOptions<AnalyticsRateLimitingOptions> options)
    : IRecommendationExposureSigner
{
    private const string PAYLOAD_PREFIX = "assetblock:recommendation-exposure:v1\n";

    public string? TryCreateToken(RecommendationExposurePayload payload)
    {
        var secret = options.Value.BffSigningSecret.Trim();
        if (string.IsNullOrEmpty(secret) || !IsWellFormed(payload))
        {
            return null;
        }

        return ComputeHmacHex(secret, Canonical(payload));
    }

    public bool TryVerify(RecommendationExposurePayload payload, string token)
    {
        var secret = options.Value.BffSigningSecret.Trim();
        if (string.IsNullOrEmpty(secret) || string.IsNullOrWhiteSpace(token) || !IsWellFormed(payload))
        {
            return false;
        }

        var expected = ComputeHmacHex(secret, Canonical(payload));
        var actual = token.Trim();
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        var actualBytes = Encoding.UTF8.GetBytes(actual);
        return expectedBytes.Length == actualBytes.Length
            && CryptographicOperations.FixedTimeEquals(expectedBytes, actualBytes);
    }

    private static bool IsWellFormed(RecommendationExposurePayload payload) =>
        payload.ExposureId != Guid.Empty
        && payload.SourceAssetId != Guid.Empty
        && !string.IsNullOrWhiteSpace(payload.RankingVersion)
        && payload.CandidateIds is { Count: > 0 }
        && payload.CandidateIds.All(id => id != Guid.Empty);

    private static string Canonical(RecommendationExposurePayload payload)
    {
        var candidatePart = string.Join(',', payload.CandidateIds.Select(id => id.ToString("D")));
        var canonical = string.Concat(
            PAYLOAD_PREFIX,
            payload.ExposureId.ToString("D"),
            "\n",
            payload.SourceAssetId.ToString("D"),
            "\n",
            payload.RankingVersion.Trim(),
            "\n",
            payload.ExpiresAt.ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture),
            "\n",
            candidatePart);
        // Bound only when present: a null audience keeps the canonical form byte-identical
        // to audience-less tokens, so public non-personal tokens stay backward-compatible.
        if (payload.AudienceUserId is { } audience && audience != Guid.Empty)
        {
            canonical += "\n" + audience.ToString("D");
        }

        return canonical;
    }

    private static string ComputeHmacHex(string secret, string canonical)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(secret), Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
