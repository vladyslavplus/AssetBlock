using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IUserStore
{
    Task<User?> GetByEmail(string email, CancellationToken cancellationToken = default);
    Task<User?> GetByIdWithLinks(Guid id, CancellationToken cancellationToken = default);
    Task<User?> GetByIdForUpdate(Guid id, CancellationToken cancellationToken = default);
    Task<User?> GetByUsernameWithLinks(string username, CancellationToken cancellationToken = default);
    /// <summary>Reads Id/Email only for transactional email (AsNoTracking, no social links).</summary>
    Task<EmailRecipient?> GetEmailRecipientById(Guid id, CancellationToken cancellationToken = default);
    Task<User> Create(string username, string email, string passwordHash, CancellationToken cancellationToken = default);
    Task<User> Update(User user, CancellationToken cancellationToken = default);
    Task<bool> UpdatePasswordHashIfMatches(Guid userId, string currentHash, string newHash, CancellationToken cancellationToken = default);
    Task<bool> ReplaceUserSocialLinks(Guid userId, IReadOnlyList<(Guid PlatformId, string Url)> links, CancellationToken cancellationToken = default);
    Task Delete(Guid userId, CancellationToken cancellationToken = default);

    Task<UserPersistedRole?> GetPersistedRole(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>PostgreSQL row lock on the user, then reads role and revision from storage.</summary>
    Task<UserPersistedRole?> LockAndReadPersistedRole(Guid userId, CancellationToken cancellationToken = default);

    /// <summary>Locks two user rows in stable ID order for role assignment transactions.</summary>
    Task<(UserPersistedRole? First, UserPersistedRole? Second)> LockUsersForRoleUpdateInOrder(
        Guid userIdA,
        Guid userIdB,
        CancellationToken cancellationToken = default);

    Task<bool> TryAssignRole(
        Guid targetUserId,
        string newRole,
        long expectedRoleRevision,
        CancellationToken cancellationToken = default);

    Task<PagedResult<AdminUserListItem>> ListForAdmin(
        string? search,
        int page,
        int pageSize,
        CancellationToken cancellationToken = default);
}
