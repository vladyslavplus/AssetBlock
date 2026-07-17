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

        user.PasswordHash = passwordHasher.Hash(request.NewPassword);
        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            await userStore.Update(user, ct);
            await auditWriter.Write(new AuditEvent(
                AuditActions.USER_PASSWORD_CHANGE,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.USER,
                request.UserId.ToString()), ct);
        }, cancellationToken);

        logger.LogInformation("Password changed for user {UserId}", request.UserId);
        return Result.Success();
    }
}
