using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Outbox;

public sealed record AssetBlobDeletePayload(Guid AssetId, string StorageKey);

public sealed record OrderCompletedPayload(
    Guid OrderId,
    Guid UserId,
    Guid? AssetId,
    Guid? BundleId,
    string ProductTitle,
    int ItemCount,
    Guid SellerId);

public sealed record NotificationDispatchPayload(
    Guid RecipientUserId,
    NotificationKind Kind,
    string HubMethod,
    string MetadataJson);

/// <summary>
/// Durable intent to schedule analysis for observed proof. Carries the exact immutable
/// evidence identities and the security selection it belongs to, so recovery rereads them
/// instead of reconstructing proof from READY state.
/// </summary>
public sealed record AnalysisSchedulingPayload(
    Guid AssetId,
    Guid AssetVersionId,
    Guid ArchiveEvidenceId,
    Guid MalwareEvidenceId,
    int SecurityGeneration,
    string ContentSha256,
    Guid? SecurityRecheckId = null,
    Guid? ParentAnalysisRunId = null);

public sealed record GetDeadLettersRequest(int Page = 1, int PageSize = 20);

public sealed record DeadLetterOutboxListItemDto(
    Guid Id,
    string Type,
    DateTimeOffset OccurredAt,
    int AttemptCount,
    DateTimeOffset? DeadLetteredAt,
    string? DeadLetterReason,
    int ReplayCount,
    DateTimeOffset? LastReplayedAt);

public sealed record ReplayDeadLetterResponseDto(
    Guid Id,
    DateTimeOffset ReplayedAt,
    int ReplayCount);
