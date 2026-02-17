using AssetBlock.Domain.Primitives.Api;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IJwtTokenService
{
    TokensResponse GenerateTokenPair(Guid userId, string email);
    Task StoreRefreshToken(Guid userId, string refreshToken, DateTime expiresAt, CancellationToken cancellationToken = default);
    Task<(Guid UserId, string Email)?> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken = default);
}
