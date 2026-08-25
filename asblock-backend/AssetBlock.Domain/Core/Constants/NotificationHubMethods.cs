namespace AssetBlock.Domain.Core.Constants;

/// <summary>SignalR hub method names for user notifications.</summary>
public static class NotificationHubMethods
{
    public const string DOWNLOAD_READY = "DownloadReady";
    public const string ASSET_SOLD = "AssetSold";
    public const string REVIEW_RECEIVED = "ReviewReceived";
    public const string ORDER_READY = "OrderReady";
    public const string ASSET_PROCESSING_UPDATED = "AssetProcessingUpdated";
    public const string ASSET_PROCESSING_READY = "AssetProcessingReady";
    public const string ASSET_PROCESSING_REJECTED = "AssetProcessingRejected";
    public const string ASSET_PROCESSING_FAILED = "AssetProcessingFailed";
}
