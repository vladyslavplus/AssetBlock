namespace AssetBlock.Domain.Core.Constants;

/// <summary>Outbox message type discriminators (wire values).</summary>
public static class OutboxMessageTypes
{
    public const string ASSET_INDEX_UPSERT = "AssetIndexUpsert";
    public const string ASSET_INDEX_DELETE = "AssetIndexDelete";
    public const string ASSET_BLOB_DELETE = "AssetBlobDelete";
    public const string PURCHASE_COMPLETED = "PurchaseCompleted";
    public const string NOTIFICATION_DISPATCH = "NotificationDispatch";

    public const int MAX_ATTEMPTS = 10;
    public const int DISPATCH_BATCH_SIZE = 50;
    public const int LEASE_MINUTES = 5;
}
