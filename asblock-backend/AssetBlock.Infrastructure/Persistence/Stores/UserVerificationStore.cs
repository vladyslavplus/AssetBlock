using AssetBlock.Domain.Abstractions.Services;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class UserVerificationStore(ApplicationDbContext dbContext) : IUserVerificationStore
{
    public Task<bool> IsEmailVerified(Guid userId, CancellationToken cancellationToken = default)
    {
        return dbContext.Users
            .AsNoTracking()
            .Where(u => u.Id == userId)
            .Select(u => u.EmailVerifiedAt != null)
            .FirstOrDefaultAsync(cancellationToken);
    }
}
