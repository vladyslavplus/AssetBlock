using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class ProcessedStripeWebhookEventStore(
    ApplicationDbContext dbContext,
    ILogger<ProcessedStripeWebhookEventStore> logger) : IProcessedStripeWebhookEventStore
{
    public async Task<bool> TryRecordEvent(
        string stripeEventId,
        string eventType,
        DateTimeOffset processedAt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stripeEventId);
        ArgumentException.ThrowIfNullOrWhiteSpace(eventType);

        var id = Guid.NewGuid();

        if (dbContext.Database.IsNpgsql())
        {
            var rowsAffected = await dbContext.Database.ExecuteSqlInterpolatedAsync(
                $"""
                INSERT INTO processed_stripe_webhook_events ("Id", "StripeEventId", "EventType", "ProcessedAt")
                VALUES ({id}, {stripeEventId}, {eventType}, {processedAt})
                ON CONFLICT ("StripeEventId") DO NOTHING
                """,
                cancellationToken);

            if (rowsAffected > 0)
            {
                logger.LogInformation("Recorded Stripe webhook event ledger claim for event {EventId}", stripeEventId);
                return true;
            }

            logger.LogInformation("Duplicate Stripe webhook event detected by ledger for event {EventId}", stripeEventId);
            return false;
        }

        var entity = new ProcessedStripeWebhookEvent
        {
            Id = id,
            StripeEventId = stripeEventId,
            EventType = eventType,
            ProcessedAt = processedAt
        };

        dbContext.ProcessedStripeWebhookEvents.Add(entity);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException ex)
        {
            dbContext.Entry(entity).State = EntityState.Detached;
            logger.LogInformation(ex, "Duplicate Stripe webhook event detected by ledger for event {EventId}", stripeEventId);
            return false;
        }
    }
}
