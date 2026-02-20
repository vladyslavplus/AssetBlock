using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace AssetBlock.Infrastructure.Services;

internal sealed class JwtTokenService(
    ApplicationDbContext dbContext,
    IOptions<JwtOptions> options,
    ILogger<JwtTokenService> logger) : IJwtTokenService
{
    public TokensResponse GenerateTokenPair(Guid userId, string username, string email, string role)
    {
        var jwtOptions = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var accessExpiresAt = DateTime.UtcNow.AddMinutes(jwtOptions.AccessTokenMinutes);
        var refreshExpiresAt = DateTimeOffset.UtcNow.AddDays(jwtOptions.RefreshTokenDays);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(ClaimTypes.Name, username),
            new(JwtClaimTypes.SUB, userId.ToString()),
            new(JwtClaimTypes.EMAIL, email),
            new(JwtClaimTypes.JTI, Guid.NewGuid().ToString()),
            new(JwtClaimTypes.ROLE, role)
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = accessExpiresAt,
            Issuer = jwtOptions.Issuer,
            Audience = jwtOptions.Audience,
            SigningCredentials = credentials
        };

        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        var accessToken = handler.CreateToken(tokenDescriptor);
        var refreshToken = Convert.ToBase64String(RandomNumberGenerator.GetBytes(64));

        logger.LogDebug("Generated token pair for user {UserId}", userId);
        return new TokensResponse(accessToken, refreshToken, accessExpiresAt, refreshExpiresAt);
    }

    public async Task StoreRefreshToken(Guid userId, string refreshToken, DateTimeOffset expiresAt, CancellationToken cancellationToken = default)
    {
        var hash = ComputeSha256Hash(refreshToken);
        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = hash,
            ExpiresAt = expiresAt,
            CreatedAt = DateTimeOffset.UtcNow
        };
        dbContext.RefreshTokens.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Stored refresh token for user {UserId}", userId);
    }

    public async Task<(Guid UserId, string Username, string Email, string Role, Guid TokenId)?> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = ComputeSha256Hash(refreshToken);
        var now = DateTimeOffset.UtcNow;
        var entity = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(rt => rt.TokenHash == hash && rt.RevokedAt == null && rt.ExpiresAt > now)
            .Select(rt => new { rt.Id, rt.UserId, rt.User.Username, rt.User.Email, rt.User.Role })
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            logger.LogDebug("Refresh token validation failed: token not found or expired");
            return null;
        }

        return (entity.UserId, entity.Username, entity.Email, entity.Role, entity.Id);
    }

    public async Task RevokeRefreshToken(Guid tokenId, CancellationToken cancellationToken = default)
    {
        var entity = await dbContext.RefreshTokens.FirstOrDefaultAsync(rt => rt.Id == tokenId, cancellationToken);
        if (entity is null)
        {
            logger.LogDebug("Attempted to revoke non-existent refresh token {TokenId}", tokenId);
            return;
        }
        entity.RevokedAt = DateTimeOffset.UtcNow;
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Revoked refresh token {TokenId}", tokenId);
    }

    private static string ComputeSha256Hash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
