namespace AssetBlock.Domain.Core.Dto.Auth;

/// <summary>Hub-only token returned for SignalR WebSocket authentication.</summary>
public sealed record HubTokenResponse(string HubToken, DateTimeOffset ExpiresAt);
