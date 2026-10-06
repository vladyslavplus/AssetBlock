using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Application.UseCases.Admin.AssignUserRole;

internal sealed class AssignUserRoleCommandHandler(
    IUserStore userStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter) : IRequestHandler<AssignUserRoleCommand, Result<UserRoleAssignmentResult>>
{
    public async Task<Result<UserRoleAssignmentResult>> Handle(AssignUserRoleCommand request, CancellationToken cancellationToken)
    {
        if (!AppRoles.AdminAssignableRoles.Contains(request.Role))
        {
            return Result.Forbidden(ErrorCodes.ERR_USER_ROLE_ASSIGNMENT_FORBIDDEN);
        }

        UserRoleAssignmentResult? result = null;
        var denied = false;
        var stale = false;

        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            (UserPersistedRole? first, UserPersistedRole? second) = await userStore.LockUsersForRoleUpdateInOrder(
                request.AdminUserId,
                request.TargetUserId,
                ct);

            if (first is null || second is null)
            {
                denied = true;
                return;
            }

            UserPersistedRole admin = first.UserId == request.AdminUserId ? first : second;
            UserPersistedRole target = first.UserId == request.TargetUserId ? first : second;

            if (admin.Role != AppRoles.ADMIN)
            {
                denied = true;
                return;
            }

            if (target.Role == AppRoles.ADMIN)
            {
                denied = true;
                return;
            }

            if (target.RoleRevision != request.ExpectedRoleRevision)
            {
                stale = true;
                return;
            }

            if (string.Equals(target.Role, request.Role, StringComparison.Ordinal))
            {
                result = new UserRoleAssignmentResult(target.UserId, target.Role, target.RoleRevision);
                return;
            }

            var assigned = await userStore.TryAssignRole(target.UserId, request.Role, request.ExpectedRoleRevision, ct);
            if (!assigned)
            {
                stale = true;
                return;
            }

            result = new UserRoleAssignmentResult(target.UserId, request.Role, request.ExpectedRoleRevision + 1);
            await auditWriter.Write(new AuditEvent(
                AuditActions.USER_ROLE_ASSIGN,
                AuditOutcome.SUCCESS,
                AuditResourceTypes.USER,
                target.UserId.ToString(),
                Metadata: new Dictionary<string, object?>
                {
                    ["role"] = request.Role,
                    ["roleRevision"] = result.RoleRevision
                }), ct);
        }, cancellationToken);

        if (denied)
        {
            return Result.Forbidden(ErrorCodes.ERR_USER_ROLE_ASSIGNMENT_FORBIDDEN);
        }

        if (stale)
        {
            return Result.Conflict(ErrorCodes.ERR_ROLE_REVISION_STALE);
        }

        return result is null
            ? Result.NotFound(ErrorCodes.ERR_USER_NOT_FOUND)
            : Result.Success(result);
    }
}
