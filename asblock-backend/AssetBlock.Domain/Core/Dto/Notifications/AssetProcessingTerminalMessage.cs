namespace AssetBlock.Domain.Core.Dto.Notifications;

/// <summary>Bounded payload for durable terminal security-processing notifications.</summary>
public sealed record AssetProcessingTerminalMessage(
    Guid NotificationId,
    Guid AssetId,
    Guid AssetVersionId,
    string ProcessingStatus,
    string AssetTitle);
