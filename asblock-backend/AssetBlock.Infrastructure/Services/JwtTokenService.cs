using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Entities;
using AssetBlock.Domain.Primitives.Api;
using AssetBlock.Domain.Primitives.AppSettingsOptions;
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
    public TokensResponse GenerateTokenPair(Guid userId, string email)
    {
        var jwtOptions = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var accessExpiresAt = DateTime.UtcNow.AddMinutes(jwtOptions.AccessTokenMinutes);
        var refreshExpiresAt = DateTime.UtcNow.AddDays(jwtOptions.RefreshTokenDays);

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(JwtClaimTypes.SUB, userId.ToString()),
            new(ClaimTypes.Email, email),
            new(JwtClaimTypes.JTI, Guid.NewGuid().ToString())
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

    public async Task StoreRefreshToken(Guid userId, string refreshToken, DateTime expiresAt, CancellationToken cancellationToken = default)
    {
        var hash = ComputeSha256Hash(refreshToken);
        var entity = new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            TokenHash = hash,
            ExpiresAt = expiresAt,
            CreatedAt = DateTime.UtcNow
        };
        dbContext.RefreshTokens.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Stored refresh token for user {UserId}", userId);
    }

    public async Task<(Guid UserId, string Email)?> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = ComputeSha256Hash(refreshToken);
        var entity = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(rt => rt.TokenHash == hash && rt.RevokedAt == null && rt.ExpiresAt > DateTime.UtcNow)
            .Select(rt => new { rt.UserId, rt.User.Email })
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            logger.LogDebug("Refresh token validation failed: token not found or expired");
            return null;
        }

        return (entity.UserId, entity.Email);
    }

    private static string ComputeSha256Hash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
