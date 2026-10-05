namespace AssetBlock.Domain.Core.Dto.Users;

public sealed record AssignUserRoleRequest(string Role, long ExpectedRoleRevision);
