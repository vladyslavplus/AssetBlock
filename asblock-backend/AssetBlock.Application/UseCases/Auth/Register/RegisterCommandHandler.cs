using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Ardalis.Result;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.Api;
using MediatR;
using AssetBlock.Domain.Core.Exceptions;
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
        User user;
        try
        {
            user = await userStore.Create(request.Email, hash, cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            logger.LogWarning("Register failed: duplicate email (concurrent)");
            return ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS);
        }

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
            try
            {
                await userStore.Delete(user.Id, cancellationToken);
            }
            catch (Exception deleteEx)
            {
                logger.LogError(deleteEx, "Rollback delete failed for user {UserId}", user.Id);
            }
            throw;
        }
    }
}
