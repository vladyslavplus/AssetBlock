using System.Data;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
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
            Status = OutboxMessageStatus.PENDING,
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
                WHERE o."Status" = {(int)OutboxMessageStatus.PENDING}
                  AND o."ProcessedAt" IS NULL
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
            .Where(m => m.Id == id && m.LockToken == lockToken && m.Status == OutboxMessageStatus.PENDING && m.ProcessedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.Status, OutboxMessageStatus.PROCESSED)
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
            .Where(m => m.Id == id && m.LockToken == lockToken && m.Status == OutboxMessageStatus.PENDING && m.ProcessedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.LastError, truncated)
                    .SetProperty(m => m.NextAttemptAt, nextAttemptAt)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockToken, (Guid?)null),
                cancellationToken);
        return updated > 0;
    }

    public async Task<bool> MarkDeadLettered(
        Guid id,
        Guid lockToken,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var boundedReason = reason.Length > 2000 ? reason[..2000] : reason;
        var error = "DEAD_LETTER: " + (reason.Length > 1980 ? reason[..1980] : reason);
        var now = DateTimeOffset.UtcNow;

        var updated = await dbContext.OutboxMessages
            .Where(m => m.Id == id && m.LockToken == lockToken && m.Status == OutboxMessageStatus.PENDING && m.ProcessedAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.Status, OutboxMessageStatus.DEAD_LETTERED)
                    .SetProperty(m => m.DeadLetteredAt, now)
                    .SetProperty(m => m.DeadLetterReason, boundedReason)
                    .SetProperty(m => m.LastError, error)
                    .SetProperty(m => m.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockToken, (Guid?)null),
                cancellationToken);
        return updated > 0;
    }

    public async Task<PagedResult<DeadLetterOutboxListItemDto>> GetDeadLetters(
        GetDeadLettersRequest request,
        CancellationToken cancellationToken = default)
    {
        var page = Math.Max(1, request.Page);
        var pageSize = Math.Clamp(request.PageSize, 1, 100);

        var query = dbContext.OutboxMessages
            .AsNoTracking()
            .Where(m => m.Status == OutboxMessageStatus.DEAD_LETTERED);

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderByDescending(m => m.DeadLetteredAt)
            .ThenByDescending(m => m.OccurredAt)
            .ThenBy(m => m.Id)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(m => new DeadLetterOutboxListItemDto(
                m.Id,
                m.Type,
                m.OccurredAt,
                m.AttemptCount,
                m.DeadLetteredAt,
                m.DeadLetterReason,
                m.ReplayCount,
                m.LastReplayedAt))
            .ToListAsync(cancellationToken);

        return new PagedResult<DeadLetterOutboxListItemDto>(items, total, page, pageSize);
    }

    public async Task<(OutboxReplayOutcome Outcome, ReplayDeadLetterResponseDto? Response)> ReplayDeadLetter(
        Guid id,
        CancellationToken cancellationToken = default)
    {
        var message = await dbContext.OutboxMessages
            .AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == id, cancellationToken);

        if (message is null)
        {
            return (OutboxReplayOutcome.NOT_FOUND, null);
        }

        if (message.Status != OutboxMessageStatus.DEAD_LETTERED)
        {
            return (OutboxReplayOutcome.NOT_DEAD_LETTERED, null);
        }

        var now = DateTimeOffset.UtcNow;
        var updated = await dbContext.OutboxMessages
            .Where(m => m.Id == id && m.Status == OutboxMessageStatus.DEAD_LETTERED)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(m => m.Status, OutboxMessageStatus.PENDING)
                    .SetProperty(m => m.DeadLetteredAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.DeadLetterReason, (string?)null)
                    .SetProperty(m => m.AttemptCount, 0)
                    .SetProperty(m => m.NextAttemptAt, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockedUntil, (DateTimeOffset?)null)
                    .SetProperty(m => m.LockToken, (Guid?)null)
                    .SetProperty(m => m.LastError, (string?)null)
                    .SetProperty(m => m.ReplayCount, m => m.ReplayCount + 1)
                    .SetProperty(m => m.LastReplayedAt, now),
                cancellationToken);

        if (updated == 0)
        {
            return (OutboxReplayOutcome.NOT_DEAD_LETTERED, null);
        }

        var response = new ReplayDeadLetterResponseDto(id, now, message.ReplayCount + 1);
        logger.LogInformation("Replayed outbox dead-letter message {OutboxId}, ReplayCount {ReplayCount}", id, response.ReplayCount);
        return (OutboxReplayOutcome.SUCCESS, response);
    }
}
