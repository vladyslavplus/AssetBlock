using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Notifications;

namespace AssetBlock.Application.UseCases.Users.ListNotifications;

public sealed record GetNotificationsQuery(Guid UserId, GetNotificationsRequest Request)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<NotificationListItemDto>>>;
