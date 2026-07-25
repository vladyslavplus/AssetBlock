namespace AssetBlock.Domain.Core.Dto.Notifications;

/// <summary>Payload for SignalR OrderReady (buyer library link).</summary>
public sealed record OrderReadyMessage(
    Guid OrderId,
    string ProductTitle,
    int ItemCount,
    Guid? AssetId,
    Guid? BundleId);
