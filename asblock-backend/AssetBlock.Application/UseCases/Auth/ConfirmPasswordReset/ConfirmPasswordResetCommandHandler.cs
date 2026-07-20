using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.ConfirmPasswordReset;

internal sealed class ConfirmPasswordResetCommandHandler(
    IUserStore userStore,
    IEmailActionStore emailActionStore,
    IEmailActionLinkProtector linkProtector,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IOutboxStore outboxStore,
    ITransactionalEmailComposer emailComposer,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<ConfirmPasswordResetCommandHandler> logger) : IRequestHandler<ConfirmPasswordResetCommand, Result>
{
    public async Task<Result> Handle(ConfirmPasswordResetCommand request, CancellationToken cancellationToken)
    {
        if (!linkProtector.TryUnprotect(request.ProtectedToken, EmailActionPurpose.PASSWORD_RESET, out var claims))
        {
            logger.LogDebug("ConfirmPasswordReset: token unprotect failed");
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_PASSWORD_RESET_CONFIRM,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        }

        var action = await emailActionStore.GetById(claims.ActionId, cancellationToken);
        if (action is null || action.Purpose != EmailActionPurpose.PASSWORD_RESET)
        {
            logger.LogDebug("ConfirmPasswordReset: action {ActionId} not found or wrong purpose", claims.ActionId);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_PASSWORD_RESET_CONFIRM,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        }

        var user = await userStore.GetByIdForUpdate(action.UserId, cancellationToken);
        if (user is null || !string.Equals(user.Email, action.TargetEmail, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("ConfirmPasswordReset: user/email mismatch for action {ActionId}", claims.ActionId);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_PASSWORD_RESET_CONFIRM,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                action.UserId.ToString(),
                new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        }

        bool consumed = false;
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            consumed = await emailActionStore.TryConsume(
                claims.ActionId,
                EmailActionPurpose.PASSWORD_RESET,
                claims.Version,
                user.Email,
                ct);

            if (consumed)
            {
                user.PasswordHash = passwordHasher.Hash(request.NewPassword);
                var emailVerifiedByPasswordReset = false;
                if (user.EmailVerifiedAt is null)
                {
                    user.EmailVerifiedAt = DateTimeOffset.UtcNow;
                    emailVerifiedByPasswordReset = true;
                }

                await userStore.Update(user, ct);
                await jwtTokenService.RevokeAllRefreshTokens(user.Id, ct);
                var notice = emailComposer.CreatePasswordChangedNotice(user.Email, user.Id);
                await outboxStore.Enqueue(OutboxMessageTypes.EMAIL_DISPATCH, notice, ct);
                await auditWriter.Write(new AuditEvent(
                    AuditActions.AUTH_PASSWORD_RESET_CONFIRM,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.USER,
                    user.Id.ToString(),
                    emailVerifiedByPasswordReset
                        ? new Dictionary<string, object?> { ["emailVerifiedByPasswordReset"] = true }
                        : null,
                    ActorTypeOverride: AuditActorType.USER,
                    ActorUserIdOverride: user.Id), ct);
            }
        }, cancellationToken);

        if (!consumed)
        {
            logger.LogDebug("ConfirmPasswordReset: TryConsume returned false for action {ActionId}", claims.ActionId);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_PASSWORD_RESET_CONFIRM,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                user.Id.ToString(),
                new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        }

        logger.LogInformation("Password reset confirmed for user {UserId}", user.Id);
        return Result.Success();
    }
}
