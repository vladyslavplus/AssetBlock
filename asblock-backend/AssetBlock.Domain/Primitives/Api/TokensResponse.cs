namespace AssetBlock.Domain.Primitives.Api;

public sealed record TokensResponse(
    string AccessToken,
    string RefreshToken,
    DateTime AccessExpiresAt,
    DateTime RefreshExpiresAt);
