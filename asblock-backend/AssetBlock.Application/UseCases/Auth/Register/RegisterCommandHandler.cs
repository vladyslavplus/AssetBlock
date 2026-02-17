using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Primitives.Api;
using Ardalis.Result;
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
            logger.LogWarning("Register failed: email already exists {Email}", request.Email);
            return ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS);
        }

        var hash = passwordHasher.Hash(request.Password);
        var user = await userStore.Create(request.Email, hash, cancellationToken);
        var tokens = jwtTokenService.GenerateTokenPair(user.Id, user.Email);
        await jwtTokenService.StoreRefreshToken(user.Id, tokens.RefreshToken, tokens.RefreshExpiresAt, cancellationToken);
        logger.LogInformation("Register succeeded: UserId={UserId}, Email={Email}", user.Id, request.Email);
        return Result.Success(tokens);
    }
}
