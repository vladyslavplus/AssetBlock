using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.Constants;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace AssetBlock.WebApi.Hubs;

/// <summary>
/// User-scoped real-time notifications; the authenticated user id claim must match SignalR Clients.User routing.
/// Accepts only hub-only tokens (hub bearer scheme). Session access tokens are rejected.
/// </summary>
[Authorize(AuthenticationSchemes = JwtAuthenticationSchemes.HUB)]
public sealed class NotificationsHub : Hub
{
    public const string ORDER_READY = NotificationHubMethods.ORDER_READY;
    public const string DOWNLOAD_READY = NotificationHubMethods.DOWNLOAD_READY;
    public const string ASSET_SOLD = NotificationHubMethods.ASSET_SOLD;
    public const string REVIEW_RECEIVED = NotificationHubMethods.REVIEW_RECEIVED;
    public const string ASSET_PROCESSING_UPDATED = NotificationHubMethods.ASSET_PROCESSING_UPDATED;
    public const string ASSET_PROCESSING_READY = NotificationHubMethods.ASSET_PROCESSING_READY;
    public const string ASSET_PROCESSING_REJECTED = NotificationHubMethods.ASSET_PROCESSING_REJECTED;
    public const string ASSET_PROCESSING_FAILED = NotificationHubMethods.ASSET_PROCESSING_FAILED;
}
