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
