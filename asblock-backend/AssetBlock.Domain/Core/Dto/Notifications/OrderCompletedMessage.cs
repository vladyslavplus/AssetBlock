namespace AssetBlock.Domain.Core.Dto.Notifications;

/// <summary>Payload for SignalR OrderCompleted (buyer receipt).</summary>
public sealed record OrderCompletedMessage(
    Guid OrderId,
    string ProductTitle,
    int ItemCount,
    Guid? AssetId,
    Guid? BundleId);
