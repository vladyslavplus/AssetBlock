using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Auth;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.Logout;

internal sealed class LogoutCommandHandler(
    IJwtTokenService jwtTokenService,
    ILogger<LogoutCommandHandler> logger) : IRequestHandler<LogoutCommand, Result>
{
    public async Task<Result> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        var validation = await jwtTokenService.ValidateRefreshToken(request.RefreshToken, cancellationToken);
        if (validation.Status != RefreshTokenValidationStatus.VALID || validation.TokenId is not { } tokenId)
        {
            logger.LogDebug("Logout: refresh token invalid, expired, or already revoked");
            return Result.Success();
        }

        await jwtTokenService.RevokeRefreshToken(tokenId, cancellationToken);
        logger.LogDebug("Logout: revoked refresh token {TokenId}", tokenId);
        return Result.Success();
    }
}
