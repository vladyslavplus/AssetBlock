using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Notifications;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.WebApi.Hubs;
using Microsoft.AspNetCore.SignalR;

namespace AssetBlock.WebApi.Services;

public sealed class RealtimeNotificationPublisher(
    IHubContext<NotificationsHub> hubContext,
    INotificationStore notificationStore,
    ILogger<RealtimeNotificationPublisher> logger) : IRealtimeNotificationPublisher
{
    private static readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public Task NotifyPurchaseCompleted(Guid userId, Guid assetId, string assetTitle, CancellationToken cancellationToken = default)
    {
        var payload = new PurchaseCompletedMessage(assetId, assetTitle);
        return PersistAndSend(
            userId,
            NotificationKind.PURCHASE_COMPLETED,
            NotificationsHub.PURCHASE_COMPLETED,
            payload,
            cancellationToken);
    }

    public Task NotifyDownloadReady(Guid userId, Guid assetId, string assetTitle, CancellationToken cancellationToken = default)
    {
        var payload = new DownloadReadyMessage(assetId, assetTitle);
        return PersistAndSend(
            userId,
            NotificationKind.DOWNLOAD_READY,
            NotificationsHub.DOWNLOAD_READY,
            payload,
            cancellationToken);
    }

    public Task NotifyAssetSold(Guid authorUserId, Guid assetId, string assetTitle, Guid buyerUserId, CancellationToken cancellationToken = default)
    {
        var payload = new AssetSoldMessage(assetId, assetTitle, buyerUserId);
        return PersistAndSend(
            authorUserId,
            NotificationKind.ASSET_SOLD,
            NotificationsHub.ASSET_SOLD,
            payload,
            cancellationToken);
    }

    public Task NotifyReviewReceived(Guid authorUserId, Guid assetId, string assetTitle, Guid reviewerUserId, int rating, CancellationToken cancellationToken = default)
    {
        var payload = new ReviewReceivedMessage(assetId, assetTitle, reviewerUserId, rating);
        return PersistAndSend(
            authorUserId,
            NotificationKind.REVIEW_RECEIVED,
            NotificationsHub.REVIEW_RECEIVED,
            payload,
            cancellationToken);
    }

    private async Task PersistAndSend<T>(Guid recipientId, NotificationKind kind, string hubMethod, T payload, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(payload, _jsonOptions);
        if (json.Length > NotificationConstraints.MAX_METADATA_JSON_LENGTH)
        {
            logger.LogWarning(
                "Skipping notification persistence: metadata length {Length} exceeds max for user {UserId}, kind {Kind}",
                json.Length,
                recipientId,
                kind);
        }
        else
        {
            var row = new UserNotification
            {
                Id = Guid.NewGuid(),
                RecipientUserId = recipientId,
                Kind = kind,
                MetadataJson = json,
                CreatedAt = DateTimeOffset.UtcNow
            };
            try
            {
                await notificationStore.Add(row, cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to persist notification before push, user {UserId}, kind {Kind}", recipientId, kind);
            }
        }

        await SendToUser(recipientId, hubMethod, payload, cancellationToken);
    }

    private async Task SendToUser<T>(Guid userId, string method, T payload, CancellationToken cancellationToken)
    {
        try
        {
            await hubContext.Clients.User(userId.ToString()).SendAsync(method, payload, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to send real-time notification {Method} to user {UserId}", method, userId);
        }
    }
}
