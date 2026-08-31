using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class EmailDeliveryStore(
    ApplicationDbContext dbContext,
    ILogger<EmailDeliveryStore> logger) : IEmailDeliveryStore
{
    public async Task<(DeliveryClaimStatus Status, Guid? ClaimToken)> TryClaimDelivery(
        Guid outboxMessageId,
        string messageId,
        string recipientAddress,
        Guid recipientUserId,
        EmailTemplateKind templateKind,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var claimToken = Guid.NewGuid();
        DateTimeOffset claimedUntil = now.Add(leaseDuration);

        // 1. Check if record exists
        OutboxEmailDelivery? existing = await dbContext.OutboxEmailDeliveries
            .AsNoTracking()
            .FirstOrDefaultAsync(d => d.OutboxMessageId == outboxMessageId, cancellationToken);

        if (existing is not null)
        {
            if (existing.DeliveredAt is not null)
            {
                return (DeliveryClaimStatus.ALREADY_DELIVERED, null);
            }

            if (existing.ClaimedUntil is not null && existing.ClaimedUntil > now)
            {
                return (DeliveryClaimStatus.CONCURRENT_CONFLICT, null);
            }

            // Claim expired or unassigned: atomically reclaim
            var updated = await dbContext.OutboxEmailDeliveries
                .Where(d => d.OutboxMessageId == outboxMessageId
                            && d.DeliveredAt == null
                            && (d.ClaimedUntil == null || d.ClaimedUntil <= now))
                .ExecuteUpdateAsync(
                    setters => setters
                        .SetProperty(d => d.ClaimToken, claimToken)
                        .SetProperty(d => d.ClaimedUntil, claimedUntil),
                    cancellationToken);

            if (updated > 0)
            {
                logger.LogInformation("Reclaimed email delivery for outbox {OutboxId}, ClaimToken {ClaimToken}", outboxMessageId, claimToken);
                return (DeliveryClaimStatus.CLAIMED, claimToken);
            }

            // Lost race on update
            OutboxEmailDelivery? reloaded = await dbContext.OutboxEmailDeliveries
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.OutboxMessageId == outboxMessageId, cancellationToken);
            if (reloaded?.DeliveredAt is not null)
            {
                return (DeliveryClaimStatus.ALREADY_DELIVERED, null);
            }
            return (DeliveryClaimStatus.CONCURRENT_CONFLICT, null);
        }

        // 2. Insert new claimed record
        var newRecord = new OutboxEmailDelivery
        {
            Id = Guid.NewGuid(),
            OutboxMessageId = outboxMessageId,
            MessageId = messageId,
            RecipientAddress = recipientAddress,
            RecipientUserId = recipientUserId,
            TemplateKind = templateKind,
            ClaimToken = claimToken,
            ClaimedUntil = claimedUntil,
            DeliveredAt = null
        };

        dbContext.OutboxEmailDeliveries.Add(newRecord);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogInformation("Claimed email delivery for outbox {OutboxId}, ClaimToken {ClaimToken}", outboxMessageId, claimToken);
            return (DeliveryClaimStatus.CLAIMED, claimToken);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            dbContext.Entry(newRecord).State = EntityState.Detached;
            OutboxEmailDelivery? current = await dbContext.OutboxEmailDeliveries
                .AsNoTracking()
                .FirstOrDefaultAsync(d => d.OutboxMessageId == outboxMessageId, cancellationToken);

            if (current?.DeliveredAt is not null)
            {
                return (DeliveryClaimStatus.ALREADY_DELIVERED, null);
            }

            return (DeliveryClaimStatus.CONCURRENT_CONFLICT, null);
        }
    }

    public async Task<bool> ConfirmDelivery(
        Guid outboxMessageId,
        Guid claimToken,
        DateTimeOffset deliveredAt,
        CancellationToken cancellationToken = default)
    {
        var updated = await dbContext.OutboxEmailDeliveries
            .Where(d => d.OutboxMessageId == outboxMessageId
                        && d.ClaimToken == claimToken
                        && d.DeliveredAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(d => d.DeliveredAt, deliveredAt)
                    .SetProperty(d => d.ClaimToken, (Guid?)null)
                    .SetProperty(d => d.ClaimedUntil, (DateTimeOffset?)null),
                cancellationToken);

        if (updated > 0)
        {
            logger.LogInformation("Confirmed email delivery for outbox {OutboxId}", outboxMessageId);
            return true;
        }

        logger.LogWarning("Failed to confirm email delivery for outbox {OutboxId}: claim token mismatch or already delivered", outboxMessageId);
        return false;
    }

    public async Task<bool> ReleaseClaim(
        Guid outboxMessageId,
        Guid claimToken,
        CancellationToken cancellationToken = default)
    {
        var updated = await dbContext.OutboxEmailDeliveries
            .Where(d => d.OutboxMessageId == outboxMessageId
                        && d.ClaimToken == claimToken
                        && d.DeliveredAt == null)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(d => d.ClaimToken, (Guid?)null)
                    .SetProperty(d => d.ClaimedUntil, (DateTimeOffset?)null),
                cancellationToken);

        return updated > 0;
    }
}
