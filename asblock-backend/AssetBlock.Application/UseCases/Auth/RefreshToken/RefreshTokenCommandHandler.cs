using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Auth;
using Ardalis.Result;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.RefreshToken;

internal sealed class RefreshTokenCommandHandler(
    IJwtTokenService jwtTokenService,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<RefreshTokenCommandHandler> logger) : IRequestHandler<RefreshTokenCommand, Result<TokensResponse>>
{
    public async Task<Result<TokensResponse>> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var validation = await jwtTokenService.ValidateRefreshToken(request.RefreshToken, cancellationToken);
        if (validation.Status == RefreshTokenValidationStatus.REVOKED_REUSED && validation.UserId is { } reusedUserId)
        {
            logger.LogWarning("Refresh token theft/reuse detected for user {UserId}! Revoking all active user sessions", reusedUserId);
            await jwtTokenService.RevokeAllRefreshTokens(reusedUserId, cancellationToken);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_REFRESH_TOKEN,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                reusedUserId.ToString(),
                ActorTypeOverride: AuditActorType.USER,
                ActorUserIdOverride: reusedUserId), cancellationToken);
            return ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_TOKEN_INVALID);
        }

        if (validation.Status != RefreshTokenValidationStatus.VALID || validation.UserId is null || validation.TokenId is null)
        {
            logger.LogDebug("Refresh token validation failed");
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_REFRESH_TOKEN,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_TOKEN_INVALID);
        }

        var userId = validation.UserId.Value;
        var tokenId = validation.TokenId.Value;
        var username = validation.Username!;
        var email = validation.Email!;
        var role = validation.Role!;
        var tokens = jwtTokenService.GenerateTokenPair(userId, username, email, role);

        var rotated = false;
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            var revoked = await jwtTokenService.RevokeRefreshToken(tokenId, ct);
            if (!revoked)
            {
                logger.LogWarning("Concurrent refresh detected for token {TokenId}; rotation aborted", tokenId);
                return;
            }

            await jwtTokenService.StoreRefreshToken(userId, tokens.RefreshToken, tokens.RefreshExpiresAt, ct);
            await auditWriter.Write(new AuditEvent(
                AuditActions.AUTH_REFRESH_TOKEN,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.USER,
                userId.ToString(),
                ActorTypeOverride: AuditActorType.USER,
                ActorUserIdOverride: userId), ct);
            rotated = true;
        }, cancellationToken);

        if (!rotated)
        {
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_REFRESH_TOKEN,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                userId.ToString(),
                ActorTypeOverride: AuditActorType.USER,
                ActorUserIdOverride: userId), cancellationToken);
            return ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_TOKEN_INVALID);
        }

        logger.LogInformation("Refresh token used successfully for user {UserId}", userId);
        return Result.Success(tokens);
    }
}
