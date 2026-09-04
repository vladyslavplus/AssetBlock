using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.BaseEntities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AssetBlock.Infrastructure.Persistence.Interceptors;

internal sealed class AuditTimestampsInterceptor(TimeProvider timeProvider) : SaveChangesInterceptor
{
    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        UpdateTimestamps(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        UpdateTimestamps(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void UpdateTimestamps(DbContext? context)
    {
        if (context is null)
        {
            return;
        }

        DateTimeOffset now = timeProvider.GetUtcNow();

        foreach (Microsoft.EntityFrameworkCore.ChangeTracking.EntityEntry entry in context.ChangeTracker.Entries())
        {
            if (entry.State == EntityState.Added)
            {
                if (entry.Entity is BaseEntity baseEntity && baseEntity.CreatedAt == default)
                {
                    baseEntity.CreatedAt = now;
                }
                else if (entry.Entity is RefreshToken refreshToken && refreshToken.CreatedAt == default)
                {
                    refreshToken.CreatedAt = now;
                }
                else if (entry.Entity is OutboxMessage outboxMessage && outboxMessage.OccurredAt == default)
                {
                    outboxMessage.OccurredAt = now;
                }
                else if (entry.Entity is CollectionItem collectionItem && collectionItem.CreatedAt == default)
                {
                    collectionItem.CreatedAt = now;
                }
                else if (entry.Entity is CheckoutReservation reservation && reservation.CreatedAt == default)
                {
                    reservation.CreatedAt = now;
                }
            }
            else if (entry.State == EntityState.Modified)
            {
                if (entry.Entity is BaseEntity baseEntity)
                {
                    baseEntity.UpdatedAt = now;
                }
            }
        }
    }
}
