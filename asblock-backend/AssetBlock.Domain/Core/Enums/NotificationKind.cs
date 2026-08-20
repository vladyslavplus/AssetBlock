namespace AssetBlock.Domain.Core.Enums;

/// <summary>
/// In-app + persisted notification category.
/// Numeric values are stable wire values stored in PostgreSQL.
/// </summary>
public enum NotificationKind
{
    DOWNLOAD_READY = 1,
    ASSET_SOLD = 2,
    REVIEW_RECEIVED = 3,
    ORDER_READY = 5
}
