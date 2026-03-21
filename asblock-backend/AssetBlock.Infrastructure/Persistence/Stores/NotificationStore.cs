using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed class NotificationStore(ApplicationDbContext dbContext, ILogger<NotificationStore> logger) : INotificationStore
{
    public async Task<UserNotification> Add(UserNotification notification, CancellationToken cancellationToken = default)
    {
        dbContext.UserNotifications.Add(notification);
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogDebug("Persisted notification {NotificationId} for user {UserId}", notification.Id, notification.RecipientUserId);
            return notification;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to persist notification for user {UserId}", notification.RecipientUserId);
            throw;
        }
    }

    public async Task<PagedResult<UserNotification>> GetPaged(Guid recipientUserId, GetNotificationsRequest request, CancellationToken cancellationToken = default)
    {
        var query = dbContext.UserNotifications.AsNoTracking().Where(n => n.RecipientUserId == recipientUserId);

        if (request.UnreadOnly == true)
        {
            query = query.Where(n => n.ReadAt == null);
        }

        var total = await query.CountAsync(cancellationToken);

        var sortBy = string.IsNullOrWhiteSpace(request.SortBy) || !GetNotificationsRequest.AllowedSortBy.Contains(request.SortBy)
            ? "CreatedAt"
            : request.SortBy.Trim();
        var sortKey = sortBy.ToUpperInvariant();
        var isDesc = request.SortDirection == SortDirection.DESC;

        query = sortKey switch
        {
            "CREATEDAT" => isDesc ? query.OrderByDescending(n => n.CreatedAt) : query.OrderBy(n => n.CreatedAt),
            _ => isDesc ? query.OrderByDescending(n => n.CreatedAt) : query.OrderBy(n => n.CreatedAt)
        };

        var items = await query
            .Skip((request.Page - 1) * request.PageSize)
            .Take(request.PageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<UserNotification>(items, total, request.Page, request.PageSize);
    }

    public async Task<bool> MarkRead(Guid recipientUserId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.UserNotifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.RecipientUserId == recipientUserId, cancellationToken);
        if (row is null)
        {
            return false;
        }

        if (row.ReadAt is not null)
        {
            return true;
        }

        row.ReadAt = DateTimeOffset.UtcNow;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark notification {NotificationId} read", notificationId);
            throw;
        }
    }
}
