using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.Outbox;

internal sealed class ModerationDecisionOutboxHandler(IRealtimeNotificationPublisher notifications, ICacheService cache, IPublicationEmbeddingEnqueuer assets) : IOutboxMessageHandler
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web);
    private sealed record Payload(Guid DecisionId, Guid SubmissionId, Guid AssetId, Guid VersionId, Guid RecipientId, long Revision, string State, string Promotion);
    public string MessageType => OutboxMessageTypes.MODERATION_DECISION;

    public async Task Handle(OutboxMessage message, CancellationToken cancellationToken)
    {
        Payload payload = JsonSerializer.Deserialize<Payload>(message.Payload, _json) ?? throw new InvalidOperationException("Invalid moderation decision event.");
        NotificationKind kind = payload.State switch
        {
            nameof(ModerationSubmissionState.APPROVED) => NotificationKind.MODERATION_APPROVED,
            nameof(ModerationSubmissionState.CHANGES_REQUESTED) => NotificationKind.MODERATION_CHANGES_REQUESTED,
            nameof(ModerationSubmissionState.REJECTED) => NotificationKind.MODERATION_REJECTED,
            _ => throw new InvalidOperationException("Invalid moderation decision state.")
        };
        if (payload.DecisionId == Guid.Empty || payload.DecisionId != message.Id || payload.SubmissionId == Guid.Empty || payload.AssetId == Guid.Empty || payload.VersionId == Guid.Empty || payload.RecipientId == Guid.Empty || payload.Revision <= 0
            || payload.Promotion is not ("PROMOTED" or "NOT_PROMOTED") || (payload.Promotion == "PROMOTED" && kind != NotificationKind.MODERATION_APPROVED))
        { throw new InvalidOperationException("Invalid moderation decision event."); }
        var metadata = JsonSerializer.Serialize(new { payload.DecisionId, payload.SubmissionId, payload.AssetId, payload.VersionId, payload.State, payload.Revision }, _json);
        await notifications.DeliverPersistedNotification(message.Id, payload.RecipientId, kind, NotificationHubMethods.MODERATION_CASE_CHANGED, metadata, cancellationToken);
        if (payload.Promotion == "PROMOTED")
        {
            await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);
            await assets.EnqueueApprovedPublicationEmbedding(payload.AssetId, cancellationToken);
        }
    }
}
