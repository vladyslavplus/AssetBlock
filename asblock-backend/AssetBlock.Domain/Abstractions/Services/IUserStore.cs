using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IUserStore
{
    Task<User?> GetByEmail(string email, CancellationToken cancellationToken = default);
    Task<User?> GetByIdWithLinks(Guid id, CancellationToken cancellationToken = default);
    Task<User?> GetByIdForUpdate(Guid id, CancellationToken cancellationToken = default);
    Task<User?> GetByUsernameWithLinks(string username, CancellationToken cancellationToken = default);
    Task<User> Create(string username, string email, string passwordHash, CancellationToken cancellationToken = default);
    Task<User> Update(User user, CancellationToken cancellationToken = default);
    Task<bool> ReplaceUserSocialLinks(Guid userId, IReadOnlyList<(Guid PlatformId, string Url)> links, CancellationToken cancellationToken = default);
    Task Delete(Guid userId, CancellationToken cancellationToken = default);
}
