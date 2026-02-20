using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Exceptions;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class UserStore(ApplicationDbContext dbContext) : IUserStore
{
    public Task<User?> GetByEmail(string email, CancellationToken cancellationToken = default)
    {
        var normalized = email.Trim().ToLowerInvariant();
        return dbContext.Users
            .AsNoTracking()
            .FirstOrDefaultAsync(u => u.Email == normalized, cancellationToken);
    }

    public async Task<User> Create(string username, string email, string passwordHash, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var user = new User
        {
            Id = Guid.NewGuid(),
            Username = username.Trim(),
            Email = email.Trim().ToLowerInvariant(),
            PasswordHash = passwordHash,
            CreatedAt = now,
            Role = AppRoles.USER
        };
        dbContext.Users.Add(user);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DuplicateEmailException();
        }
        return user;
    }

    public async Task Delete(Guid userId, CancellationToken cancellationToken = default)
    {
        var user = await dbContext.Users.FirstOrDefaultAsync(u => u.Id == userId, cancellationToken);
        if (user is null)
        {
            return;
        }
        dbContext.Users.Remove(user);
        await dbContext.SaveChangesAsync(cancellationToken);
    }
}
