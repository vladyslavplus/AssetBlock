using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Users;

namespace AssetBlock.Application.UseCases.Admin.AssignUserRole;

public sealed record AssignUserRoleCommand(
    Guid AdminUserId,
    Guid TargetUserId,
    string Role,
    long ExpectedRoleRevision) : IRequest<Result<UserRoleAssignmentResult>>;
