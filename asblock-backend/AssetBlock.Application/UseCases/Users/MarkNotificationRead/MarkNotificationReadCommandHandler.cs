using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Users.MarkNotificationRead;

internal sealed class MarkNotificationReadCommandHandler(
    INotificationStore notificationStore,
    ILogger<MarkNotificationReadCommandHandler> logger)
    : IRequestHandler<MarkNotificationReadCommand, Result>
{
    public async Task<Result> Handle(MarkNotificationReadCommand request, CancellationToken cancellationToken)
    {
        var ok = await notificationStore.MarkRead(request.UserId, request.NotificationId, cancellationToken);
        if (!ok)
        {
            return Result.NotFound(ErrorCodes.ERR_NOTIFICATION_NOT_FOUND);
        }

        logger.LogDebug("Marked notification {NotificationId} read for user {UserId}", request.NotificationId, request.UserId);
        return Result.Success();
    }
}
