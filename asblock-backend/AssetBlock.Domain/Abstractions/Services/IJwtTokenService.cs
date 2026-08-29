using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.Domain.Core.Primitives.Api;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IJwtTokenService
{
    TokensResponse GenerateTokenPair(Guid userId, string username, string email, string role);
    Task StoreRefreshToken(Guid userId, string refreshToken, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    Task<RefreshTokenValidationResult> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken = default);
    /// <summary>Revokes a single refresh token atomically. Returns true if the token was actively revoked, false if not found or already revoked.</summary>
    Task<bool> RevokeRefreshToken(Guid tokenId, CancellationToken cancellationToken = default);
    /// <summary>Revokes every active refresh token for the user. Does not log or return token values.</summary>
    Task RevokeAllRefreshTokens(Guid userId, CancellationToken cancellationToken = default);
    /// <summary>Deletes expired refresh tokens in batches up to batchSize. Preserves unexpired revoked tokens for grace window and reuse detection.</summary>
    Task<int> CleanupExpiredTokens(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default);
}
