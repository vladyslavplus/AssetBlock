using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Ardalis.Result;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.Login;

internal sealed class LoginCommandHandler(
    IUserStore userStore,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<LoginCommandHandler> logger) : IRequestHandler<LoginCommand, Result<TokensResponse>>
{
    public async Task<Result<TokensResponse>> Handle(LoginCommand request, CancellationToken cancellationToken)
    {
        var user = await userStore.GetByEmail(request.Email, cancellationToken);
        if (user is null)
        {
            logger.LogWarning("Login failed: invalid credentials");
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_LOGIN,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS);
        }

        if (!passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            logger.LogWarning("Login failed: invalid credentials for user {UserId}", user.Id);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_LOGIN,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error<TokensResponse>(ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS);
        }

        var tokens = jwtTokenService.GenerateTokenPair(user.Id, user.Username, user.Email, user.Role);
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            await jwtTokenService.StoreRefreshToken(user.Id, tokens.RefreshToken, tokens.RefreshExpiresAt, ct);
            await auditWriter.Write(new AuditEvent(
                AuditActions.AUTH_LOGIN,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.USER,
                user.Id.ToString(),
                ActorTypeOverride: AuditActorType.USER,
                ActorUserIdOverride: user.Id), ct);
        }, cancellationToken);

        logger.LogInformation("Login succeeded for user {UserId}", user.Id);
        return Result.Success(tokens);
    }
}
