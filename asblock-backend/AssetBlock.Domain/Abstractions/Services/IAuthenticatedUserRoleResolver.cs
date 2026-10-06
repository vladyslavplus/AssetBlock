using AssetBlock.Domain.Core.Dto.Users;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAuthenticatedUserRoleResolver
{
    Task<UserPersistedRole?> Resolve(Guid userId, CancellationToken cancellationToken = default);
}
