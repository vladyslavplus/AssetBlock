using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Ardalis.Result;
using AssetBlock.Domain.Core.Primitives.Api;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.Register;

internal sealed class RegisterCommandHandler(
    IUserStore userStore,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    ILogger<RegisterCommandHandler> logger) : IRequestHandler<RegisterCommand, Result<TokensResponse>>
{
    public async Task<Result<TokensResponse>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var existing = await userStore.GetByEmail(request.Email, cancellationToken);
        if (existing is not null)
        {
            logger.LogWarning("Register failed: email already exists");
            return ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS);
        }

        var hash = passwordHasher.Hash(request.Password);
        var user = await userStore.Create(request.Email, hash, cancellationToken);
        try
        {
            var tokens = jwtTokenService.GenerateTokenPair(user.Id, user.Email);
            await jwtTokenService.StoreRefreshToken(user.Id, tokens.RefreshToken, tokens.RefreshExpiresAt, cancellationToken);
            logger.LogInformation("Register succeeded: UserId={UserId}", user.Id);
            return Result.Success(tokens);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "StoreRefreshToken failed after user create; rolling back user {UserId}", user.Id);
            await userStore.Delete(user.Id, cancellationToken);
            throw;
        }
    }
}
