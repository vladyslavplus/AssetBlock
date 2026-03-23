namespace AssetBlock.Domain.Core.Dto.Notifications;

/// <summary>Payload for SignalR PurchaseCompleted (buyer).</summary>
public sealed record PurchaseCompletedMessage(Guid AssetId, string AssetTitle);
