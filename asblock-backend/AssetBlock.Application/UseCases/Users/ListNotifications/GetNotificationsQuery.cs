using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Notifications;
using MediatR;

namespace AssetBlock.Application.UseCases.Users.ListNotifications;

public sealed record GetNotificationsQuery(Guid UserId, GetNotificationsRequest Request)
    : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<NotificationListItemDto>>>;
