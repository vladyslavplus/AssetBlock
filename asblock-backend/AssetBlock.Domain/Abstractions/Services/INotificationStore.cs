using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface INotificationStore
{
    Task<UserNotification> Add(UserNotification notification, CancellationToken cancellationToken = default);

    Task<UserNotification?> GetBySourceOutboxMessageId(Guid sourceOutboxMessageId, CancellationToken cancellationToken = default);

    Task<PagedResult<UserNotification>> GetPaged(Guid recipientUserId, GetNotificationsRequest request, CancellationToken cancellationToken = default);

    /// <summary>Returns false if the row does not exist or belongs to another user.</summary>
    Task<bool> MarkRead(Guid recipientUserId, Guid notificationId, CancellationToken cancellationToken = default);

    /// <summary>Returns false if the row does not exist or belongs to another user.</summary>
    Task<bool> MarkUnread(Guid recipientUserId, Guid notificationId, CancellationToken cancellationToken = default);

    /// <summary>Sets ReadAt on all unread notifications for the recipient; returns rows updated.</summary>
    Task<int> MarkAllRead(Guid recipientUserId, CancellationToken cancellationToken = default);
}
