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

    public Task<User?> GetByIdWithLinks(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Users
            .AsNoTracking()
            .Include(u => u.SocialLinks).ThenInclude(sl => sl.Platform)
            .FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    public Task<User?> GetByIdForUpdate(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.Users.FirstOrDefaultAsync(u => u.Id == id, cancellationToken);
    }

    public Task<User?> GetByUsernameWithLinks(string username, CancellationToken cancellationToken = default)
    {
        var normalized = username.Trim();
        return dbContext.Users
            .AsNoTracking()
            .Include(u => u.SocialLinks).ThenInclude(sl => sl.Platform)
            .FirstOrDefaultAsync(u => EF.Functions.ILike(u.Username, normalized), cancellationToken);
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

    public async Task<User> Update(User user, CancellationToken cancellationToken = default)
    {
        try
        {
            user.UpdatedAt = DateTimeOffset.UtcNow;
            var entry = dbContext.Entry(user);
            if (entry.State == EntityState.Detached)
            {
                dbContext.Users.Update(user);
            }

            await dbContext.SaveChangesAsync(cancellationToken);
            return user;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new DuplicateUsernameException();
        }
    }

    public async Task<bool> ReplaceUserSocialLinks(Guid userId, IReadOnlyList<(Guid PlatformId, string Url)> links, CancellationToken cancellationToken = default)
    {
        var userExists = await dbContext.Users.AnyAsync(u => u.Id == userId, cancellationToken);
        if (!userExists)
        {
            return false;
        }

        await dbContext.Set<UserSocialLink>()
            .Where(x => x.UserId == userId)
            .ExecuteDeleteAsync(cancellationToken);

        var now = DateTimeOffset.UtcNow;
        foreach (var (platformId, url) in links)
        {
            dbContext.Set<UserSocialLink>().Add(new UserSocialLink
            {
                Id = Guid.NewGuid(),
                UserId = userId,
                PlatformId = platformId,
                Url = url.Trim(),
                CreatedAt = now
            });
        }

        await dbContext.SaveChangesAsync(cancellationToken);
        return true;
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
