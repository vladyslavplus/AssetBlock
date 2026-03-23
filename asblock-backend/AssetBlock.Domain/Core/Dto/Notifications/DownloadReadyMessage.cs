namespace AssetBlock.Domain.Core.Dto.Notifications;

/// <summary>Payload for SignalR DownloadReady (buyer; same moment as purchase for sync checkout).</summary>
public sealed record DownloadReadyMessage(Guid AssetId, string AssetTitle);
