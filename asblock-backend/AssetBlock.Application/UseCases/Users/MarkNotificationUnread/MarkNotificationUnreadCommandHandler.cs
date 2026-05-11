using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Users.MarkNotificationUnread;

internal sealed class MarkNotificationUnreadCommandHandler(
    INotificationStore notificationStore,
    ILogger<MarkNotificationUnreadCommandHandler> logger)
    : IRequestHandler<MarkNotificationUnreadCommand, Result>
{
    public async Task<Result> Handle(MarkNotificationUnreadCommand request, CancellationToken cancellationToken)
    {
        var ok = await notificationStore.MarkUnread(request.UserId, request.NotificationId, cancellationToken);
        if (!ok)
        {
            return Result.NotFound(ErrorCodes.ERR_NOTIFICATION_NOT_FOUND);
        }

        logger.LogDebug("Marked notification {NotificationId} unread for user {UserId}", request.NotificationId, request.UserId);
        return Result.Success();
    }
}
