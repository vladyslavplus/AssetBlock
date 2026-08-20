using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.RequestPasswordReset;

internal sealed class RequestPasswordResetCommandHandler(
    IUserStore userStore,
    IEmailActionStore emailActionStore,
    IOutboxStore outboxStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<RequestPasswordResetCommandHandler> logger) : IRequestHandler<RequestPasswordResetCommand, Result>
{
    public async Task<Result> Handle(RequestPasswordResetCommand request, CancellationToken cancellationToken)
    {
        var user = await userStore.GetByEmail(request.Email.Trim(), cancellationToken);
        if (user is null)
        {
            // Always succeed to prevent email enumeration
            return Result.Success();
        }

        var existing = await emailActionStore.GetCurrent(
            user.Id,
            EmailActionPurpose.PASSWORD_RESET,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (emailActionStore.IsInCooldown(existing, EmailActionConstants.ResendCooldown, now))
        {
            logger.LogDebug("RequestPasswordReset: cooldown active for user {UserId}", user.Id);
            // Still return success - do not reveal cooldown to caller
            return Result.Success();
        }

        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            var action = await emailActionStore.IssueOrReplace(
                user.Id,
                EmailActionPurpose.PASSWORD_RESET,
                user.Email,
                EmailActionConstants.PasswordResetExpiry,
                ct);
            await outboxStore.Enqueue(
                OutboxMessageTypes.EMAIL_ACTION_DISPATCH,
                new EmailActionDispatchPayload(action.Id, action.Version, user.Id, EmailTemplateKind.PASSWORD_RESET),
                ct);
            await auditWriter.Write(new AuditEvent(
                AuditActions.AUTH_PASSWORD_RESET_REQUEST,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.USER,
                user.Id.ToString(),
                ActorTypeOverride: AuditActorType.ANONYMOUS), ct);
        }, cancellationToken);

        logger.LogInformation("Password reset requested for user {UserId}", user.Id);
        return Result.Success();
    }
}
