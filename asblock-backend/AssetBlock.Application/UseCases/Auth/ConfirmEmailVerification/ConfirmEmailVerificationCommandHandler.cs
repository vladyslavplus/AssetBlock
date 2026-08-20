using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.ConfirmEmailVerification;

internal sealed class ConfirmEmailVerificationCommandHandler(
    IUserStore userStore,
    IEmailActionStore emailActionStore,
    IEmailActionLinkProtector linkProtector,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<ConfirmEmailVerificationCommandHandler> logger) : IRequestHandler<ConfirmEmailVerificationCommand, Result>
{
    public async Task<Result> Handle(ConfirmEmailVerificationCommand request, CancellationToken cancellationToken)
    {
        if (!linkProtector.TryUnprotect(request.ProtectedToken, EmailActionPurpose.EMAIL_VERIFICATION, out var claims))
        {
            logger.LogDebug("ConfirmEmailVerification: token unprotect failed");
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_EMAIL_VERIFICATION,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        }

        var action = await emailActionStore.GetById(claims.ActionId, cancellationToken);
        if (action is null || action.Purpose != EmailActionPurpose.EMAIL_VERIFICATION)
        {
            logger.LogDebug("ConfirmEmailVerification: action {ActionId} not found or wrong purpose", claims.ActionId);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_EMAIL_VERIFICATION,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        }

        var user = await userStore.GetByIdForUpdate(action.UserId, cancellationToken);
        if (user is null || !string.Equals(user.Email, action.TargetEmail, StringComparison.OrdinalIgnoreCase))
        {
            logger.LogDebug("ConfirmEmailVerification: user/email mismatch for action {ActionId}", claims.ActionId);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_EMAIL_VERIFICATION,
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
                EmailActionPurpose.EMAIL_VERIFICATION,
                claims.Version,
                user.Email,
                ct);

            if (consumed)
            {
                user.EmailVerifiedAt = DateTimeOffset.UtcNow;
                await userStore.Update(user, ct);
                await auditWriter.Write(new AuditEvent(
                    AuditActions.AUTH_EMAIL_VERIFICATION,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.USER,
                    user.Id.ToString(),
                    ActorTypeOverride: AuditActorType.USER,
                    ActorUserIdOverride: user.Id), ct);
            }
        }, cancellationToken);

        if (!consumed)
        {
            logger.LogDebug("ConfirmEmailVerification: TryConsume returned false for action {ActionId}", claims.ActionId);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_EMAIL_VERIFICATION,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                user.Id.ToString(),
                new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        }

        logger.LogInformation("Email verified for user {UserId}", user.Id);
        return Result.Success();
    }
}
