using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Users;

namespace AssetBlock.Infrastructure.Services;

internal sealed class AuthenticatedUserRoleResolver(IUserStore userStore) : IAuthenticatedUserRoleResolver
{
    public Task<UserPersistedRole?> Resolve(Guid userId, CancellationToken cancellationToken = default) =>
        userStore.GetPersistedRole(userId, cancellationToken);
}
