using AssetBlock.Domain.Core.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AssetBlock.WebApi.Hubs;

/// <summary>
/// User-scoped real-time notifications; the authenticated user id claim must match SignalR Clients.User routing.
/// </summary>
[Authorize]
public sealed class NotificationsHub : Hub
{
    public const string PURCHASE_COMPLETED = NotificationHubMethods.PURCHASE_COMPLETED;
    public const string DOWNLOAD_READY = NotificationHubMethods.DOWNLOAD_READY;
    public const string ASSET_SOLD = NotificationHubMethods.ASSET_SOLD;
    public const string REVIEW_RECEIVED = NotificationHubMethods.REVIEW_RECEIVED;
}
