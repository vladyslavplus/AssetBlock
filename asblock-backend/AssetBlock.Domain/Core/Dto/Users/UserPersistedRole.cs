namespace AssetBlock.Domain.Core.Dto.Users;

public sealed record UserPersistedRole(Guid UserId, string Role, long RoleRevision);
