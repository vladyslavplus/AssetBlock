namespace AssetBlock.Domain.Core.Dto.Notifications;

/// <summary>Payload for SignalR AssetSold (asset author).</summary>
public sealed record AssetSoldMessage(Guid AssetId, string AssetTitle, Guid BuyerUserId);
