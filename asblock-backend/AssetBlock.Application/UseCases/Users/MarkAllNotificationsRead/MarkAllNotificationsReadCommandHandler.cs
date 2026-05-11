using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Users.MarkAllNotificationsRead;

internal sealed class MarkAllNotificationsReadCommandHandler(
    INotificationStore notificationStore,
    ILogger<MarkAllNotificationsReadCommandHandler> logger)
    : IRequestHandler<MarkAllNotificationsReadCommand, Result<MarkAllNotificationsReadResponseDto>>
{
    public async Task<Result<MarkAllNotificationsReadResponseDto>> Handle(
        MarkAllNotificationsReadCommand request,
        CancellationToken cancellationToken)
    {
        var updated = await notificationStore.MarkAllRead(request.UserId, cancellationToken);
        logger.LogDebug("Marked {Count} notifications read for user {UserId}", updated, request.UserId);
        return Result.Success(new MarkAllNotificationsReadResponseDto(updated));
    }
}
