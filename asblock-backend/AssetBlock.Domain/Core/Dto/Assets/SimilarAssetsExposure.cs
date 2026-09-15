namespace AssetBlock.Domain.Core.Dto.Assets;

/// <summary>
/// Server-issued bounded proof that a later impression or click refers to this similar-assets response.
/// Token is an HMAC; it is not a proof that a human looked at the cards.
/// </summary>
public sealed record SimilarAssetsExposure(
    Guid Id,
    string RankingVersion,
    DateTimeOffset ExpiresAt,
    string Token);
