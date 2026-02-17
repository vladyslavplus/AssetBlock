using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IUserStore
{
    Task<User?> GetByEmail(string email, CancellationToken cancellationToken = default);
    Task<User> Create(string email, string passwordHash, CancellationToken cancellationToken = default);
    Task Delete(Guid userId, CancellationToken cancellationToken = default);
}
