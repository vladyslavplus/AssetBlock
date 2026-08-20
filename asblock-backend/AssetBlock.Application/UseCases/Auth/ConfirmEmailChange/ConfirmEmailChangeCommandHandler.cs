using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.ConfirmEmailChange;

internal sealed class ConfirmEmailChangeCommandHandler(
    IUserStore userStore,
    IEmailActionStore emailActionStore,
    IEmailActionLinkProtector linkProtector,
    IJwtTokenService jwtTokenService,
    IOutboxStore outboxStore,
    ITransactionalEmailComposer emailComposer,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<ConfirmEmailChangeCommandHandler> logger) : IRequestHandler<ConfirmEmailChangeCommand, Result>
{
    public async Task<Result> Handle(ConfirmEmailChangeCommand request, CancellationToken cancellationToken)
    {
        if (!linkProtector.TryUnprotect(request.ProtectedToken, EmailActionPurpose.EMAIL_CHANGE, out var claims))
        {
            logger.LogDebug("ConfirmEmailChange: token unprotect failed");
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_EMAIL_CHANGE_CONFIRM,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        }

        var action = await emailActionStore.GetById(claims.ActionId, cancellationToken);
        if (action is null || action.Purpose != EmailActionPurpose.EMAIL_CHANGE)
        {
            logger.LogDebug("ConfirmEmailChange: action {ActionId} not found or wrong purpose", claims.ActionId);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_EMAIL_CHANGE_CONFIRM,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                Metadata: new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        }

        var user = await userStore.GetByIdForUpdate(action.UserId, cancellationToken);
        if (user is null)
        {
            logger.LogDebug("ConfirmEmailChange: user {UserId} not found", action.UserId);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_EMAIL_CHANGE_CONFIRM,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                action.UserId.ToString(),
                new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        }

        var oldEmail = user.Email;
        var targetEmail = action.TargetEmail;

        if (string.Equals(oldEmail, targetEmail, StringComparison.OrdinalIgnoreCase))
        {
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_EMAIL_CHANGE_CONFIRM,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                user.Id.ToString(),
                new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        }

        bool consumed = false;
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                consumed = await emailActionStore.TryConsume(
                    claims.ActionId,
                    EmailActionPurpose.EMAIL_CHANGE,
                    claims.Version,
                    targetEmail,
                    ct);

                if (consumed)
                {
                    user.Email = targetEmail;
                    user.EmailVerifiedAt = DateTimeOffset.UtcNow;
                    await userStore.Update(user, ct);
                    await jwtTokenService.RevokeAllRefreshTokens(user.Id, ct);
                    var notice = emailComposer.CreateEmailChangedNotice(oldEmail, user.Id);
                    await outboxStore.Enqueue(OutboxMessageTypes.EMAIL_DISPATCH, notice, ct);
                    await auditWriter.Write(new AuditEvent(
                        AuditActions.AUTH_EMAIL_CHANGE_CONFIRM,
                        AuditOutcome.SUCCESS,
                        AuditResourceTypes.USER,
                        user.Id.ToString(),
                        ActorTypeOverride: AuditActorType.USER,
                        ActorUserIdOverride: user.Id), ct);
                }
            }, cancellationToken);
        }
        catch (DuplicateEmailException)
        {
            logger.LogWarning("ConfirmEmailChange: duplicate email conflict for user {UserId}", user.Id);
            return Result.Conflict(ErrorCodes.ERR_EMAIL_CHANGE_TARGET_TAKEN);
        }

        if (!consumed)
        {
            logger.LogDebug("ConfirmEmailChange: TryConsume returned false for action {ActionId}", claims.ActionId);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_EMAIL_CHANGE_CONFIRM,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                user.Id.ToString(),
                new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED },
                ActorTypeOverride: AuditActorType.ANONYMOUS), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_INVALID_OR_EXPIRED);
        }

        logger.LogInformation("Email changed for user {UserId}", user.Id);
        return Result.Success();
    }
}
