using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Users.ChangePassword;

internal sealed class ChangePasswordCommandHandler(
    IUserStore userStore,
    IPasswordHasher passwordHasher,
    IJwtTokenService jwtTokenService,
    IOutboxStore outboxStore,
    ITransactionalEmailComposer emailComposer,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ILogger<ChangePasswordCommandHandler> logger) : IRequestHandler<ChangePasswordCommand, Result>
{
    public async Task<Result> Handle(ChangePasswordCommand request, CancellationToken cancellationToken)
    {
        var user = await userStore.GetByIdForUpdate(request.UserId, cancellationToken);
        if (user is null)
        {
            return Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND);
        }

        if (!passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
        {
            logger.LogWarning("ChangePassword failed: invalid current password for user {UserId}", request.UserId);
            await auditWriter.WriteBestEffort(new AuditEvent(
                AuditActions.USER_PASSWORD_CHANGE,
                AuditOutcome.FAILURE,
                AuditResourceTypes.USER,
                request.UserId.ToString(),
                new Dictionary<string, object?> { ["reasonCode"] = ErrorCodes.ERR_AUTH_CURRENT_PASSWORD_INVALID }), cancellationToken);
            return ResultError.Error(ErrorCodes.ERR_AUTH_CURRENT_PASSWORD_INVALID);
        }

        var newHash = passwordHasher.Hash(request.NewPassword);
        var userEmail = user.Email;
        var userId = user.Id;

        user.PasswordHash = newHash;
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            await userStore.Update(user, ct);
            await jwtTokenService.RevokeAllRefreshTokens(userId, ct);
            var notice = emailComposer.CreatePasswordChangedNotice(userEmail, userId);
            await outboxStore.Enqueue(OutboxMessageTypes.EMAIL_DISPATCH, notice, ct);
            await auditWriter.Write(new AuditEvent(
                AuditActions.USER_PASSWORD_CHANGE,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.USER,
                userId.ToString()), ct);
        }, cancellationToken);

        logger.LogInformation("Password changed for user {UserId}", userId);
        return Result.Success();
    }
}
