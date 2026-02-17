using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AssetBlock.WebApi.Hubs;

/// <summary>
/// Notifications for payment completion and download ready.
/// Clients subscribe by user id (authenticated).
/// </summary>
[Authorize]
public sealed class NotificationsHub : Hub
{
    public const string PURCHASE_COMPLETED = "PurchaseCompleted";
    public const string DOWNLOAD_READY = "DownloadReady";
}
