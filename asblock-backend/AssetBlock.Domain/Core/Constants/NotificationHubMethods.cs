namespace AssetBlock.Domain.Core.Constants;

/// <summary>SignalR hub method names for user notifications.</summary>
public static class NotificationHubMethods
{
    public const string PURCHASE_COMPLETED = "PurchaseCompleted";
    public const string DOWNLOAD_READY = "DownloadReady";
    public const string ASSET_SOLD = "AssetSold";
    public const string REVIEW_RECEIVED = "ReviewReceived";
}
