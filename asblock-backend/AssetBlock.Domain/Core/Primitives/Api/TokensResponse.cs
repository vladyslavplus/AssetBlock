namespace AssetBlock.Domain.Core.Primitives.Api;

public sealed record TokensResponse(
    string AccessToken,
    string RefreshToken,
    DateTimeOffset AccessExpiresAt,
    DateTimeOffset RefreshExpiresAt);
