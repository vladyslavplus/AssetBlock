using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Auth;
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
    ILogger<JwtTokenService> logger,
    TimeProvider? timeProvider = null) : IJwtTokenService
{
    private static readonly TimeSpan _reusedGraceWindow = TimeSpan.FromSeconds(15);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public TokensResponse GenerateTokenPair(Guid userId, string username, string email, string role)
    {
        JwtOptions jwtOptions = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        DateTime accessExpiresAt = now.AddMinutes(jwtOptions.AccessTokenMinutes).UtcDateTime;
        DateTimeOffset refreshExpiresAt = now.AddDays(jwtOptions.RefreshTokenDays);

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

    public HubTokenResponse GenerateHubToken(Guid userId)
    {
        JwtOptions jwtOptions = options.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwtOptions.Key));
        var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        DateTimeOffset expiresAt = _timeProvider.GetUtcNow().AddSeconds(jwtOptions.HubTokenSeconds);

        // Intentionally minimal claims: no email, role, or name.
        // The distinct hub audience and token_use claim prevent this token from
        // authorising any REST endpoint that validates audience or token_use.
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId.ToString()),
            new(JwtClaimTypes.SUB, userId.ToString()),
            new(JwtClaimTypes.JTI, Guid.NewGuid().ToString()),
            new(JwtClaimTypes.TOKEN_USE, JwtClaimValues.TOKEN_USE_SIGNALR)
        };

        var tokenDescriptor = new SecurityTokenDescriptor
        {
            Subject = new ClaimsIdentity(claims),
            Expires = expiresAt.UtcDateTime,
            Issuer = jwtOptions.Issuer,
            Audience = jwtOptions.HubAudience,
            SigningCredentials = credentials
        };

        var handler = new Microsoft.IdentityModel.JsonWebTokens.JsonWebTokenHandler();
        var hubToken = handler.CreateToken(tokenDescriptor);
        logger.LogDebug("Generated hub token for user {UserId}", userId);
        return new HubTokenResponse(hubToken, expiresAt);
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
            CreatedAt = _timeProvider.GetUtcNow()
        };
        dbContext.RefreshTokens.Add(entity);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Stored refresh token for user {UserId}", userId);
    }

    public async Task<RefreshTokenValidationResult> ValidateRefreshToken(string refreshToken, CancellationToken cancellationToken = default)
    {
        var hash = ComputeSha256Hash(refreshToken);
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var entity = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(rt => rt.TokenHash == hash && rt.ExpiresAt > now)
            .Select(rt => new { rt.Id, rt.UserId, rt.User.Username, rt.User.Email, rt.User.Role, rt.RevokedAt })
            .FirstOrDefaultAsync(cancellationToken);

        if (entity is null)
        {
            logger.LogDebug("Refresh token validation failed: token not found or expired");
            return new RefreshTokenValidationResult(RefreshTokenValidationStatus.NOT_FOUND_OR_EXPIRED);
        }

        if (entity.RevokedAt != null)
        {
            if (now - entity.RevokedAt.Value <= _reusedGraceWindow)
            {
                logger.LogDebug("Refresh token {TokenId} was recently revoked within grace window {GraceSeconds}s; rejecting without full session revocation", entity.Id, _reusedGraceWindow.TotalSeconds);
                return new RefreshTokenValidationResult(RefreshTokenValidationStatus.NOT_FOUND_OR_EXPIRED, entity.UserId);
            }

            logger.LogWarning("Refresh token reuse detected for token {TokenId} and user {UserId} (revoked at {RevokedAt})", entity.Id, entity.UserId, entity.RevokedAt);
            return new RefreshTokenValidationResult(
                RefreshTokenValidationStatus.REVOKED_REUSED,
                entity.UserId,
                entity.Username,
                entity.Email,
                entity.Role,
                entity.Id);
        }

        return new RefreshTokenValidationResult(
            RefreshTokenValidationStatus.VALID,
            entity.UserId,
            entity.Username,
            entity.Email,
            entity.Role,
            entity.Id);
    }

    public async Task<bool> RevokeRefreshToken(Guid tokenId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        var affected = await dbContext.RefreshTokens
            .Where(rt => rt.Id == tokenId && rt.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(rt => rt.RevokedAt, now), cancellationToken);

        if (affected == 0)
        {
            logger.LogDebug("Attempted to revoke non-existent or already-revoked refresh token {TokenId}", tokenId);
            return false;
        }

        logger.LogDebug("Revoked refresh token {TokenId}", tokenId);
        return true;
    }

    public async Task RevokeAllRefreshTokens(Guid userId, CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        await dbContext.RefreshTokens
            .Where(rt => rt.UserId == userId && rt.RevokedAt == null)
            .ExecuteUpdateAsync(setters => setters.SetProperty(rt => rt.RevokedAt, now), cancellationToken);
        logger.LogDebug("Revoked all active refresh tokens for user {UserId}", userId);
    }

    public async Task<int> CleanupExpiredTokens(DateTimeOffset now, int batchSize, CancellationToken cancellationToken = default)
    {
        if (batchSize <= 0)
        {
            return 0;
        }

        List<Guid> expiredIds = await dbContext.RefreshTokens
            .AsNoTracking()
            .Where(r => r.ExpiresAt <= now)
            .OrderBy(r => r.ExpiresAt)
            .Select(r => r.Id)
            .Take(batchSize)
            .ToListAsync(cancellationToken);

        if (expiredIds.Count == 0)
        {
            return 0;
        }

        var deleted = await dbContext.RefreshTokens
            .Where(r => expiredIds.Contains(r.Id))
            .ExecuteDeleteAsync(cancellationToken);

        logger.LogInformation("Cleaned up {DeletedCount} expired refresh tokens", deleted);
        return deleted;
    }

    private static string ComputeSha256Hash(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToBase64String(hash);
    }
}
