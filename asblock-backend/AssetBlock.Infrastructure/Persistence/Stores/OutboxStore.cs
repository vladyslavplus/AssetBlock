using System.Data;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class OutboxStore(ApplicationDbContext dbContext, ILogger<OutboxStore> logger) : IOutboxStore
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public async Task Enqueue(string type, object payload, CancellationToken cancellationToken = default)
    {
        var message = new OutboxMessage
        {
            Id = Guid.NewGuid(),
            Type = type,
            Payload = JsonSerializer.Serialize(payload, payload.GetType(), _jsonOptions),
            OccurredAt = DateTimeOffset.UtcNow,
            AttemptCount = 0
        };
        dbContext.OutboxMessages.Add(message);
        await dbContext.SaveChangesAsync(cancellationToken);
        logger.LogDebug("Enqueued outbox {Type} {OutboxId}", type, message.Id);
    }

    public async Task<IReadOnlyList<OutboxMessage>> ClaimPendingBatch(
        int batchSize,
        TimeSpan lease,
        CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var leaseUntil = now.Add(lease);
        var lockToken = Guid.NewGuid();

        await using var tx = await dbContext.Database.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken);

        var rows = await dbContext.OutboxMessages
            .FromSqlInterpolated($"""
                SELECT *
                FROM outbox_messages AS o
                WHERE o."ProcessedAt" IS NULL
                  AND o."AttemptCount" < {OutboxMessageTypes.MAX_ATTEMPTS}
                  AND (o."NextAttemptAt" IS NULL OR o."NextAttemptAt" <= {now})
                  AND (o."LockedUntil" IS NULL OR o."LockedUntil" < {now})
                ORDER BY o."OccurredAt"
                FOR UPDATE SKIP LOCKED
                LIMIT {batchSize}
                """)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (rows.Count == 0)
        {
            await tx.CommitAsync(cancellationToken);
            return [];
        }

        var ids = rows.Select(r => r.Id).ToList();
        await dbContext.OutboxMessages
            .Where(m => ids.Contains(m.Id))
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.LockedUntil, leaseUntil)
                    .SetProperty(m => m.LockToken, lockToken)
                    .SetProperty(m => m.AttemptCount, m => m.AttemptCount + 1),
                cancellationToken);

        var claimed = await dbContext.OutboxMessages
            .AsNoTracking()
            .Where(m => ids.Contains(m.Id))
            .OrderBy(m => m.OccurredAt)
            .ToListAsync(cancellationToken);

        await tx.CommitAsync(cancellationToken);
        return claimed;
    }

    public async Task<bool> MarkProcessed(Guid id, Guid lockToken, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        var updated = await dbContext.OutboxMessages
            .Where(m => m.Id == id && m.LockToken == lockToken && m.ProcessedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.ProcessedAt, now)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockToken, (Guid?)null)
                    .SetProperty(m => m.LastError, (string?)null),
                cancellationToken);
        return updated > 0;
    }

    public async Task<bool> MarkFailed(
        Guid id,
        Guid lockToken,
        string error,
        DateTimeOffset nextAttemptAt,
        CancellationToken cancellationToken = default)
    {
        var truncated = error.Length > 2000 ? error[..2000] : error;
        var updated = await dbContext.OutboxMessages
            .Where(m => m.Id == id && m.LockToken == lockToken && m.ProcessedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.LastError, truncated)
                    .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockToken, (Guid?)null),
                cancellationToken);
        return updated > 0;
    }
}
