namespace AssetBlock.Domain.Core.Dto.Notifications;

public sealed record NotificationListItemDto(
    Guid Id,
    string Kind,
    string MetadataJson,
    DateTimeOffset CreatedAt,
    DateTimeOffset? ReadAt);
