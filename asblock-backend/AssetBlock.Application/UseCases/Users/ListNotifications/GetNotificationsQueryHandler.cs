using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Notifications;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Users.ListNotifications;

internal sealed class GetNotificationsQueryHandler(
    INotificationStore notificationStore,
    ILogger<GetNotificationsQueryHandler> logger)
    : IRequestHandler<GetNotificationsQuery, Result<Domain.Core.Dto.Paging.PagedResult<NotificationListItemDto>>>
{
    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<NotificationListItemDto>>> Handle(GetNotificationsQuery request, CancellationToken cancellationToken)
    {
        var paged = await notificationStore.GetPaged(request.UserId, request.Request, cancellationToken);
        var items = paged.Items
            .Select(n => new NotificationListItemDto(
                n.Id,
                n.Kind.ToString(),
                n.MetadataJson,
                n.CreatedAt,
                n.ReadAt))
            .ToList();

        var result = new Domain.Core.Dto.Paging.PagedResult<NotificationListItemDto>(items, paged.TotalCount, paged.Page, paged.PageSize);
        logger.LogDebug("Listed {Count} notifications for user {UserId} (page {Page})", items.Count, request.UserId, request.Request.Page);
        return Result.Success(result);
    }
}
