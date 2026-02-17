using AssetBlock.Domain.Core.Primitives.Api;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IJwtTokenService
{
    TokensResponse GenerateTokenPair(Guid userId, string email);
    Task StoreRefreshToken(Guid userId, string refreshToken, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    Task<(Guid UserId, string Email, Guid TokenId)?> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken = default);
    Task RevokeRefreshToken(Guid tokenId, CancellationToken cancellationToken = default);
}
