using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Ardalis.Result;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.Api;
using MediatR;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.Register;

internal sealed class RegisterCommandHandler(
    IUserStore userStore,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<RegisterCommandHandler> logger) : IRequestHandler<RegisterCommand, Result<TokensResponse>>
{
    public async Task<Result<TokensResponse>> Handle(RegisterCommand request, CancellationToken cancellationToken)
    {
        var existing = await userStore.GetByEmail(request.Email, cancellationToken);
        if (existing is not null)
        {
            logger.LogWarning("Register failed: email already exists");
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_REGISTER,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return Result.Conflict(ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS);
        }

        var hash = passwordHasher.Hash(request.Password);
        User? user = null;
        TokensResponse? tokens = null;

        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                user = await userStore.Create(request.Username, request.Email, hash, ct);
                tokens = jwtTokenService.GenerateTokenPair(user.Id, user.Username, user.Email, user.Role);
                await jwtTokenService.StoreRefreshToken(user.Id, tokens.RefreshToken, tokens.RefreshExpiresAt, ct);
                await auditWriter.Write(new AuditEvent(
                    AuditActions.AUTH_REGISTER,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.USER,
                    user.Id.ToString(),
                    ActorTypeOverride: AuditActorType.USER,
                    ActorUserIdOverride: user.Id), ct);
            }, cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            logger.LogWarning("Register failed: duplicate email (concurrent)");
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_REGISTER,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return Result.Conflict(ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS);
        }
        catch (DuplicateUsernameException)
        {
            logger.LogWarning("Register failed: duplicate username (concurrent)");
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_REGISTER,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_USERNAME_ALREADY_EXISTS },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return Result.Conflict(ErrorCodes.ERR_USERNAME_ALREADY_EXISTS);
        }

        logger.LogInformation("Register succeeded: UserId={UserId}", user!.Id);
        return Result.Success(tokens!);
    }
}
