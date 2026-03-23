using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AssetBlock.WebApi.Hubs;

/// <summary>
/// User-scoped real-time notifications; the authenticated user id claim must match SignalR Clients.User routing.
/// </summary>
[Authorize]
public sealed class NotificationsHub : Hub
{
    public const string PURCHASE_COMPLETED = "PurchaseCompleted";
    public const string DOWNLOAD_READY = "DownloadReady";
    public const string ASSET_SOLD = "AssetSold";
    public const string REVIEW_RECEIVED = "ReviewReceived";
}
