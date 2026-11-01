using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Outbox;

namespace AssetBlock.Infrastructure.Persistence.Stores;

internal sealed partial class ModerationFoundationStore
{
    private void EnqueueLifecycleNotification(ModerationSubmission submission, NotificationKind kind, Guid? reviewerId, DateTimeOffset now)
    {
        var payload = new ModerationLifecyclePayload(submission.Id, submission.AssetId, submission.AssetVersionId, submission.OwnerUserId,
            reviewerId, submission.State, submission.CaseRevision, kind, submission.Kind == ModerationSubmissionKind.APPEAL);
        dbContext.OutboxMessages.Add(new OutboxMessage
        {
            Id = ModerationEventIdentity.Event(submission.Id, submission.CaseRevision, kind, payload.IsAppeal),
            Type = OutboxMessageTypes.MODERATION_LIFECYCLE,
            Payload = JsonSerializer.Serialize(payload, CoverageReviewState.JsonOptions),
            OccurredAt = now
        });
    }
}
