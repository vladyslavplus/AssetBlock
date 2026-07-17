using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Ardalis.Result;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using MediatR;
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
        var payload = await jwtTokenService.ValidateRefreshToken(request.RefreshToken, cancellationToken);
        if (payload is null)
        {
            logger.LogDebug("Refresh token validation failed");
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_REFRESH_TOKEN,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_TOKEN_INVALID);
        }

        (Guid userId, var username, var email, var role, Guid tokenId) = payload.Value;
        var tokens = jwtTokenService.GenerateTokenPair(userId, username, email, role);

        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            await jwtTokenService.RevokeRefreshToken(tokenId, ct);
            await jwtTokenService.StoreRefreshToken(userId, tokens.RefreshToken, tokens.RefreshExpiresAt, ct);
            await auditWriter.Write(new AuditEvent(
                AuditActions.AUTH_REFRESH_TOKEN,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.USER,
                userId.ToString(),
                ActorTypeOverride: AuditActorType.USER,
                ActorUserIdOverride: userId), ct);
        }, cancellationToken);

        logger.LogInformation("Refresh token used successfully for user {UserId}", userId);
        return Result.Success(tokens);
    }
}
