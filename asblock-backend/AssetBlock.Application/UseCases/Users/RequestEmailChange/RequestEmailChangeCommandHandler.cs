using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Users.RequestEmailChange;

internal sealed class RequestEmailChangeCommandHandler(
    IUserStore userStore,
    IPasswordHasher passwordHasher,
    IEmailActionStore emailActionStore,
    IOutboxStore outboxStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<RequestEmailChangeCommandHandler> logger) : IRequestHandler<RequestEmailChangeCommand, Result>
{
    public async Task<Result> Handle(RequestEmailChangeCommand request, CancellationToken cancellationToken)
    {
        var user = await userStore.GetByIdForUpdate(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND);
        }

        if (!passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            logger.LogWarning("RequestEmailChange: invalid current password for user {UserId}", request.UserId);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.AUTH_EMAIL_CHANGE_REQUEST,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                request.UserId.ToString(),
                new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_AUTH_CURRENT_PASSWORD_INVALID }), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_AUTH_CURRENT_PASSWORD_INVALID);
        }

        var normalizedNew = request.NewEmail.Trim().ToLowerInvariant();

        var existingWithNewEmail = await userStore.GetByEmail(normalizedNew, cancellationToken);
        if (existingWithNewEmail is not null)
        {
            if (existingWithNewEmail.Id == request.UserId)
            {
                return ResultError.Error(ErrorCodes.ERR_EMAIL_CHANGE_SAME_AS_CURRENT);
            }
            return Result.Conflict(ErrorCodes.ERR_EMAIL_CHANGE_TARGET_TAKEN);
        }

        // Also guard against same email when GetByEmail uses exact match but emails differ only in case
        if (string.Equals(user.Email, normalizedNew, StringComparison.OrdinalIgnoreCase))
        {
            return ResultError.Error(ErrorCodes.ERR_EMAIL_CHANGE_SAME_AS_CURRENT);
        }

        var existing = await emailActionStore.GetCurrent(
            request.UserId,
            EmailActionPurpose.EMAIL_CHANGE,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (emailActionStore.IsInCooldown(existing, EmailActionConstants.ResendCooldown, now))
        {
            logger.LogDebug("RequestEmailChange: cooldown active for user {UserId}", request.UserId);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_COOLDOWN);
        }

        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            var action = await emailActionStore.IssueOrReplace(
                request.UserId,
                EmailActionPurpose.EMAIL_CHANGE,
                normalizedNew,
                EmailActionConstants.EmailChangeExpiry,
                ct);
            await outboxStore.Enqueue(
                OutboxMessageTypes.EMAIL_ACTION_DISPATCH,
                new EmailActionDispatchPayload(action.Id, action.Version, request.UserId, EmailTemplateKind.EMAIL_CHANGE_CONFIRMATION),
                ct);
            await auditWriter.Write(new AuditEvent(
                AuditActions.AUTH_EMAIL_CHANGE_REQUEST,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.USER,
                request.UserId.ToString()), ct);
        }, cancellationToken);

        logger.LogInformation("Email change requested for user {UserId}", request.UserId);
        return Result.Success();
    }
}
