using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.WebApi.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AssetBlock.WebApi.Services;

public sealed class AssetProcessingRealtimePublisher(
    IHubContext<NotificationsHub> hubContext,
    ILogger<AssetProcessingRealtimePublisher> logger
) : IAssetProcessingRealtimePublisher
{
    public async Task PublishJobUpdated(
        Guid ownerUserId,
        AssetProcessingUpdateMessage message,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await hubContext.Clients
                .User(ownerUserId.ToString())
                .SendAsync(NotificationsHub.ASSET_PROCESSING_UPDATED, message, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Ignored on cancellation.
        }
        catch (Exception ex)
        {
            logger.LogWarning(
                ex,
                "Failed to dispatch real-time job invalidation hint {JobId} to user {UserId}",
                message.JobId,
                ownerUserId);
        }
    }
}
