using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Ardalis.Result;
using AssetBlock.Domain.Core.Primitives.Api;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.Login;

internal sealed class LoginCommandHandler(
    IUserStore userStore,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    ILogger<LoginCommandHandler> logger) : IRequestHandler<LoginCommand, Result<TokensResponse>>
{
    public async Task<Result<TokensResponse>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var user = await userStore.GetByEmail(request.Email, cancellationToken);
        if (user is null)
        {
            logger.LogWarning("Login failed: invalid credentials");
            return ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS);
        }

        if (!passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            logger.LogWarning("Login failed: invalid credentials for user {UserId}", user.Id);
            return ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS);
        }

        var tokens = jwtTokenService.GenerateTokenPair(user.Id, user.Email);
        await jwtTokenService.StoreRefreshToken(user.Id, tokens.RefreshToken, tokens.RefreshExpiresAt, cancellationToken);
        logger.LogInformation("Login succeeded for user {UserId}", user.Id);
        return Result.Success(tokens);
    }
}
