namespace AssetBlock.Domain.Core.Dto.Users;

public sealed record UserRoleAssignmentResult(
    Guid UserId,
    string Role,
    long RoleRevision);
