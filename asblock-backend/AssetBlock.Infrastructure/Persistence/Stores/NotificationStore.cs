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
        try
        {
            dbContext.UserNotifications.Add(notification);
            await dbContext.SaveChangesAsync(cancellationToken);
            logger.LogDebug("Persisted notification {NotificationId} for user {UserId}", notification.Id, notification.RecipientUserId);
            return notification;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            dbContext.Entry(notification).State = EntityState.Detached;
            logger.LogError(ex, "Failed to persist notification for user {UserId}", notification.RecipientUserId);
            throw;
        }
    }

    public async Task<UserNotification?> GetBySourceOutboxMessageId(Guid sourceOutboxMessageId, CancellationToken cancellationToken = default)
    {
        return await dbContext.UserNotifications
            .AsNoTracking()
            .FirstOrDefaultAsync(n => n.SourceOutboxMessageId == sourceOutboxMessageId, cancellationToken);
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
            "CREATEDAT" => isDesc ? query.OrderByDescending(n => n.CreatedAt).ThenBy(n => n.Id) : query.OrderBy(n => n.CreatedAt).ThenBy(n => n.Id),
            "READAT" => isDesc ? query.OrderByDescending(n => n.ReadAt).ThenBy(n => n.Id) : query.OrderBy(n => n.ReadAt).ThenBy(n => n.Id),
            _ => throw new ArgumentOutOfRangeException(nameof(request.SortBy), sortBy, $"Unexpected sort key after validation: {sortBy}.")
        };

        var page = Math.Max(PagedRequest.DEFAULT_PAGE, request.Page);
        var pageSize = Math.Clamp(request.PageSize, PagedRequest.MIN_PAGE_SIZE, PagedRequest.MAX_PAGE_SIZE);
        var items = await query
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        return new PagedResult<UserNotification>(items, total, page, pageSize);
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

    public async Task<bool> MarkUnread(Guid recipientUserId, Guid notificationId, CancellationToken cancellationToken = default)
    {
        var row = await dbContext.UserNotifications
            .FirstOrDefaultAsync(n => n.Id == notificationId && n.RecipientUserId == recipientUserId, cancellationToken);
        if (row is null)
        {
            return false;
        }

        if (row.ReadAt is null)
        {
            return true;
        }

        row.ReadAt = null;
        try
        {
            await dbContext.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark notification {NotificationId} unread", notificationId);
            throw;
        }
    }

    public async Task<int> MarkAllRead(Guid recipientUserId, CancellationToken cancellationToken = default)
    {
        var now = DateTimeOffset.UtcNow;
        try
        {
            var affected = await dbContext.UserNotifications
                .Where(n => n.RecipientUserId == recipientUserId && n.ReadAt == null)
                .ExecuteUpdateAsync(
                    setters => setters.SetProperty(n => n.ReadAt, now),
                    cancellationToken);
            if (affected > 0)
            {
                logger.LogDebug("Marked {Count} notifications read for user {UserId}", affected, recipientUserId);
            }

            return affected;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to mark all notifications read for user {UserId}", recipientUserId);
            throw;
        }
    }
}
