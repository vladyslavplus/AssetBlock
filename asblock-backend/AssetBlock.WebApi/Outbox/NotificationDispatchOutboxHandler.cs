using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.Outbox;

namespace AssetBlock.WebApi.Outbox;

internal sealed class NotificationDispatchOutboxHandler(
    IRealtimeNotificationPublisher realtimeNotifications) : IOutboxMessageHandler
{
    private static readonly JsonSerializerOptions _json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public string MessageType => OutboxMessageTypes.NOTIFICATION_DISPATCH;

    public async Task Handle(OutboxMessage message, CancellationToken cancellationToken)
    {
        var payload = JsonSerializer.Deserialize<NotificationDispatchPayload>(message.Payload, _json)
            ?? throw new InvalidOperationException("Invalid NotificationDispatch payload.");

        await realtimeNotifications.DeliverPersistedNotification(
            message.Id,
            payload.RecipientUserId,
            payload.Kind,
            payload.HubMethod,
            payload.MetadataJson,
            cancellationToken);
    }
}
