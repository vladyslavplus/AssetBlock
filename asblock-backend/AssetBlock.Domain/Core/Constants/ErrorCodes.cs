namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Centralized error codes for API responses. All constants UPPER_SNAKE_CASE, values prefixed with ERR_.
/// </summary>
public static class ErrorCodes
{
    public const string ERR_AUTH_INVALID_CREDENTIALS = "ERR_AUTH_INVALID_CREDENTIALS";
    public const string ERR_AUTH_TOKEN_EXPIRED = "ERR_AUTH_TOKEN_EXPIRED";
    public const string ERR_AUTH_TOKEN_INVALID = "ERR_AUTH_TOKEN_INVALID";
    public const string ERR_AUTH_USER_NOT_FOUND = "ERR_AUTH_USER_NOT_FOUND";
    public const string ERR_AUTH_EMAIL_ALREADY_EXISTS = "ERR_AUTH_EMAIL_ALREADY_EXISTS";

    public const string ERR_CATEGORY_NOT_FOUND = "ERR_CATEGORY_NOT_FOUND";
    public const string ERR_TAG_NOT_FOUND = "ERR_TAG_NOT_FOUND";
    public const string ERR_TAG_ALREADY_EXISTS = "ERR_TAG_ALREADY_EXISTS";
    public const string ERR_ASSET_TAG_ALREADY_EXISTS = "ERR_ASSET_TAG_ALREADY_EXISTS";
    public const string ERR_ASSET_TAG_NOT_FOUND = "ERR_ASSET_TAG_NOT_FOUND";
    public const string ERR_ASSET_NOT_FOUND = "ERR_ASSET_NOT_FOUND";
    public const string ERR_ASSET_UPLOAD_FAILED = "ERR_ASSET_UPLOAD_FAILED";

    public const string ERR_PURCHASE_NOT_FOUND = "ERR_PURCHASE_NOT_FOUND";
    public const string ERR_PURCHASE_ACCESS_DENIED = "ERR_PURCHASE_ACCESS_DENIED";
    public const string ERR_PAYMENT_FAILED = "ERR_PAYMENT_FAILED";
    public const string ERR_STRIPE_URLS_NOT_CONFIGURED = "ERR_STRIPE_URLS_NOT_CONFIGURED";
    public const string ERR_DOWNLOAD_LIMIT_EXCEEDED = "ERR_DOWNLOAD_LIMIT_EXCEEDED";
    public const string ERR_CANNOT_PURCHASE_OWN_ASSET = "ERR_CANNOT_PURCHASE_OWN_ASSET";

    public const string ERR_CATEGORY_SLUG_EXISTS = "ERR_CATEGORY_SLUG_EXISTS";

    public const string ERR_REVIEW_ALREADY_EXISTS = "ERR_REVIEW_ALREADY_EXISTS";
    public const string ERR_REVIEW_TIME_WINDOW_EXPIRED = "ERR_REVIEW_TIME_WINDOW_EXPIRED";
    public const string ERR_ASSET_NOT_PURCHASED = "ERR_ASSET_NOT_PURCHASED";
    public const string ERR_REVIEW_NOT_FOUND = "ERR_REVIEW_NOT_FOUND";
    public const string ERR_CANNOT_REVIEW_OWN_ASSET = "ERR_CANNOT_REVIEW_OWN_ASSET";
    public const string ERR_REVIEW_CREATE_FAILED = "ERR_REVIEW_CREATE_FAILED";

    public const string ERR_FILE_REQUIRED = "ERR_FILE_REQUIRED";
    public const string ERR_NOT_FOUND = "ERR_NOT_FOUND";
    public const string ERR_FORBIDDEN = "ERR_FORBIDDEN";
    public const string ERR_BAD_REQUEST = "ERR_BAD_REQUEST";
    public const string ERR_CONFLICT = "ERR_CONFLICT";
}
