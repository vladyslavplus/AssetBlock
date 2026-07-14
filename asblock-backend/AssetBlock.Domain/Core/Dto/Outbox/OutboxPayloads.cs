using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto.Outbox;

public sealed record AssetIndexUpsertPayload(Guid AssetId);

public sealed record AssetIndexDeletePayload(Guid AssetId);

public sealed record AssetBlobDeletePayload(Guid AssetId, string StorageKey);

public sealed record PurchaseCompletedPayload(Guid PurchaseId, Guid UserId, Guid AssetId, string AssetTitle, Guid AuthorId);

public sealed record NotificationDispatchPayload(
    Guid RecipientUserId,
    NotificationKind Kind,
    string HubMethod,
    string MetadataJson);
