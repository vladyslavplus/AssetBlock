using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Infrastructure.Outbox;

internal sealed record ModerationLifecyclePayload(Guid SubmissionId, Guid AssetId, Guid VersionId, Guid OwnerId,
    Guid? ReviewerId, ModerationSubmissionState State, long Revision, NotificationKind Kind, bool IsAppeal);
