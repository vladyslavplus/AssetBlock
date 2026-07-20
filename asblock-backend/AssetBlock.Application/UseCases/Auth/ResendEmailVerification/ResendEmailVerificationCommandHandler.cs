using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Auth.ResendEmailVerification;

internal sealed class ResendEmailVerificationCommandHandler(
    IUserStore userStore,
    IEmailActionStore emailActionStore,
    IOutboxStore outboxStore,
    IUnitOfWork unitOfWork,
    ILogger<ResendEmailVerificationCommandHandler> logger) : IRequestHandler<ResendEmailVerificationCommand, Result>
{
    public async Task<Result> Handle(ResendEmailVerificationCommand request, CancellationToken cancellationToken)
    {
        var user = await userStore.GetByIdForUpdate(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND);
        }

        if (user.EmailVerifiedAt.HasValue)
        {
            return Result.Success();
        }

        var existing = await emailActionStore.GetCurrent(
            request.UserId,
            EmailActionPurpose.EMAIL_VERIFICATION,
            cancellationToken);

        var now = DateTimeOffset.UtcNow;
        if (emailActionStore.IsInCooldown(existing, EmailActionConstants.ResendCooldown, now))
        {
            logger.LogDebug("ResendEmailVerification cooldown active for user {UserId}", request.UserId);
            return ResultError.Error(ErrorCodes.ERR_EMAIL_ACTION_COOLDOWN);
        }

        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            var action = await emailActionStore.IssueOrReplace(
                request.UserId,
                EmailActionPurpose.EMAIL_VERIFICATION,
                user.Email,
                EmailActionConstants.VerificationExpiry,
                ct);
            await outboxStore.Enqueue(
                OutboxMessageTypes.EMAIL_ACTION_DISPATCH,
                new EmailActionDispatchPayload(action.Id, action.Version, request.UserId, EmailTemplateKind.EMAIL_VERIFICATION),
                ct);
        }, cancellationToken);

        logger.LogInformation("Email verification resent for user {UserId}", request.UserId);
        return Result.Success();
    }
}
