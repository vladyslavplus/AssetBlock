using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class EmailActionStore(ApplicationDbContext dbContext) : IEmailActionStore
{
    public Task<EmailAction?> GetById(Guid id, CancellationToken cancellationToken = default)
    {
        return dbContext.EmailActions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);
    }

    public Task<EmailAction?> GetCurrent(Guid userId, EmailActionPurpose purpose, CancellationToken cancellationToken = default)
    {
        return dbContext.EmailActions
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.UserId == userId && a.Purpose == purpose, cancellationToken);
    }

    public async Task<EmailAction> IssueOrReplace(
        Guid userId,
        EmailActionPurpose purpose,
        string targetEmail,
        TimeSpan lifetime,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedEmail = targetEmail.Trim().ToLowerInvariant();
        var purposeValue = purpose.ToString();
        var id = Guid.NewGuid();
        var version = Guid.NewGuid();
        var expiresAt = now.Add(lifetime);

        // Atomic upsert: concurrent first inserts cannot race on IX_email_actions_UserId_Purpose.
        await dbContext.Database.ExecuteSqlAsync(
            $"""
            INSERT INTO email_actions ("Id", "UserId", "Purpose", "TargetEmail", "Version", "CreatedAt", "ExpiresAt", "ConsumedAt", "LastSentAt")
            VALUES ({id}, {userId}, {purposeValue}, {normalizedEmail}, {version}, {now}, {expiresAt}, NULL, {now})
            ON CONFLICT ("UserId", "Purpose") DO UPDATE SET
                "TargetEmail" = EXCLUDED."TargetEmail",
                "Version" = EXCLUDED."Version",
                "CreatedAt" = EXCLUDED."CreatedAt",
                "ExpiresAt" = EXCLUDED."ExpiresAt",
                "ConsumedAt" = NULL,
                "LastSentAt" = EXCLUDED."LastSentAt"
            """,
            cancellationToken);

        return await dbContext.EmailActions
            .AsNoTracking()
            .SingleAsync(a => a.UserId == userId && a.Purpose == purpose, cancellationToken);
    }

    public async Task<bool> TryConsume(
        Guid actionId,
        EmailActionPurpose purpose,
        Guid version,
        string expectedTargetEmail,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var normalizedEmail = expectedTargetEmail.Trim().ToLowerInvariant();
        var affected = await dbContext.EmailActions
            .Where(a =>
                a.Id == actionId
                && a.Purpose == purpose
                && a.Version == version
                && a.ConsumedAt == null
                && a.ExpiresAt > now
                && a.TargetEmail == normalizedEmail)
            .ExecuteUpdateAsync(
                setters => setters.SetProperty(a => a.ConsumedAt, now),
                cancellationToken);

        return affected == 1;
    }

    public bool IsInCooldown(EmailAction? action, TimeSpan cooldown, DateTimeOffset now)
    {
        if (action?.LastSentAt is null)
        {
            return false;
        }

        return now - action.LastSentAt.Value < cooldown;
    }
}
