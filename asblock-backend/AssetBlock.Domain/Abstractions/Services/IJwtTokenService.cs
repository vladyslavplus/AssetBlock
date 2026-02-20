using AssetBlock.Domain.Core.Primitives.Api;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IJwtTokenService
{
    TokensResponse GenerateTokenPair(Guid userId, string username, string email, string role);
    Task StoreRefreshToken(Guid userId, string refreshToken, DateTimeOffset expiresAt, CancellationToken cancellationToken = default);
    Task<(Guid UserId, string Username, string Email, string Role, Guid TokenId)?> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken = default);
    Task RevokeRefreshToken(Guid tokenId, CancellationToken cancellationToken = default);
}
