namespace AssetBlock.Domain.Core.Dto.Users;

public sealed record AdminUserListItem(
    Guid Id,
    string Username,
    string Email,
    string Role,
    long RoleRevision);
