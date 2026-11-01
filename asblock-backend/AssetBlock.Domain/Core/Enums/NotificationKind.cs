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
    ORDER_READY = 5,
    ASSET_PROCESSING_READY = 6,
    ASSET_PROCESSING_REJECTED = 7,
    ASSET_PROCESSING_FAILED = 8,
    CODE_ANALYSIS_COMPLETED = 9,
    MODERATION_SUBMITTED = 10,
    MODERATION_REVIEW_STARTED = 11,
    MODERATION_CHANGES_REQUESTED = 12,
    MODERATION_APPROVED = 13,
    MODERATION_REJECTED = 14,
    MODERATION_WITHDRAWN = 15,
    MODERATION_APPEAL_UPHELD = 16
}
