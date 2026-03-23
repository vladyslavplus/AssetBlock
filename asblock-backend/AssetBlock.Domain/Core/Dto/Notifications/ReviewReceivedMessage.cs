namespace AssetBlock.Domain.Core.Dto.Notifications;

/// <summary>Payload for SignalR ReviewReceived (asset author).</summary>
public sealed record ReviewReceivedMessage(Guid AssetId, string AssetTitle, Guid ReviewerUserId, int Rating);
