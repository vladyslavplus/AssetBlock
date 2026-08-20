namespace AssetBlock.Domain.Core.Dto.Notifications;

/// <summary>Payload for SignalR seller sale notice after an order completes (one message per order).</summary>
public sealed record OrderSoldMessage(
    Guid OrderId,
    string ProductTitle,
    int ItemCount,
    Guid BuyerUserId,
    Guid? AssetId,
    Guid? BundleId);
