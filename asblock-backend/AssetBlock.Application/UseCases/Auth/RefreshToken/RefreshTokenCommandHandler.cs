using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Ardalis.Result;
using AssetBlock.Domain.Core.Primitives.Api;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.RefreshToken;

internal sealed class RefreshTokenCommandHandler(
    IJwtTokenService jwtTokenService,
    ILogger<RefreshTokenCommandHandler> logger) : IRequestHandler<RefreshTokenCommand, Result<TokensResponse>>
{
    public async Task<Result<TokensResponse>> Handle(RefreshTokenCommand request, CancellationToken cancellationToken)
    {
        var payload = await jwtTokenService.ValidateRefreshToken(request.RefreshToken, cancellationToken);
        if (payload is null)
        {
            logger.LogDebug("Refresh token validation failed");
            return ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_TOKEN_INVALID);
        }

        (Guid userId, var username, var email, var role, Guid tokenId) = payload.Value;
        await jwtTokenService.RevokeRefreshToken(tokenId, cancellationToken);
        var tokens = jwtTokenService.GenerateTokenPair(userId, username, email, role);
        await jwtTokenService.StoreRefreshToken(userId, tokens.RefreshToken, tokens.RefreshExpiresAt, cancellationToken);
        logger.LogInformation("Refresh token used successfully for user {UserId}", userId);
        return Result.Success(tokens);
    }
}
