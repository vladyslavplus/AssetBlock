using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.Logout;

internal sealed class LogoutCommandHandler(
    IJwtTokenService jwtTokenService,
    ILogger<LogoutCommandHandler> logger) : IRequestHandler<LogoutCommand, Result>
{
    public async Task<Result> Handle(LogoutCommand request, CancellationToken cancellationToken)
    {
        var payload = await jwtTokenService.ValidateRefreshToken(request.RefreshToken, cancellationToken);
        if (payload is null)
        {
            logger.LogDebug("Logout: refresh token invalid, expired, or already revoked");
            return Result.Success();
        }

        (_, _, _, _, Guid tokenId) = payload.Value;
        await jwtTokenService.RevokeRefreshToken(tokenId, cancellationToken);
        logger.LogDebug("Logout: revoked refresh token {TokenId}", tokenId);
        return Result.Success();
    }
}
