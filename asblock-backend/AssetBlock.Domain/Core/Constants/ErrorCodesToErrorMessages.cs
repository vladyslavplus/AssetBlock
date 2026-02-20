namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Maps each error code to a user-facing message. Single place for code-to-message mapping.
/// </summary>
public static class ErrorCodesToErrorMessages
{
    private static readonly IReadOnlyDictionary<string, string> _map = new Dictionary<string, string>
    {
        { ErrorCodes.ERR_AUTH_INVALID_CREDENTIALS, "Invalid email or password." },
        { ErrorCodes.ERR_AUTH_TOKEN_EXPIRED, "Token has expired." },
        { ErrorCodes.ERR_AUTH_TOKEN_INVALID, "Invalid token." },
        { ErrorCodes.ERR_AUTH_USER_NOT_FOUND, "User not found." },
        { ErrorCodes.ERR_AUTH_EMAIL_ALREADY_EXISTS, "An account with this email already exists." },
        { ErrorCodes.ERR_CATEGORY_NOT_FOUND, "Category not found." },
        { ErrorCodes.ERR_ASSET_NOT_FOUND, "Asset not found." },
        { ErrorCodes.ERR_ASSET_UPLOAD_FAILED, "Failed to upload asset." },
        { ErrorCodes.ERR_PURCHASE_NOT_FOUND, "Purchase not found." },
        { ErrorCodes.ERR_PURCHASE_ACCESS_DENIED, "You do not have access to this asset." },
        { ErrorCodes.ERR_PAYMENT_FAILED, "Payment failed." },
        { ErrorCodes.ERR_STRIPE_URLS_NOT_CONFIGURED, "Payment redirect URLs are not configured. Contact support." },
        { ErrorCodes.ERR_DOWNLOAD_LIMIT_EXCEEDED, "Download limit for this asset has been reached. Please try again later." },
        { ErrorCodes.ERR_CANNOT_PURCHASE_OWN_ASSET, "You cannot purchase your own asset." },
        { ErrorCodes.ERR_CATEGORY_SLUG_EXISTS, "A category with this slug already exists." },
        { ErrorCodes.ERR_FILE_REQUIRED, "File is required." },
        { ErrorCodes.ERR_REVIEW_ALREADY_EXISTS, "You have already reviewed this asset." },
        { ErrorCodes.ERR_REVIEW_TIME_WINDOW_EXPIRED, "Reviews can only be left within 14 days of purchase." },
        { ErrorCodes.ERR_ASSET_NOT_PURCHASED, "You must purchase this asset before reviewing it." },
        { ErrorCodes.ERR_REVIEW_NOT_FOUND, "Review not found." },
        { ErrorCodes.ERR_CANNOT_REVIEW_OWN_ASSET, "You cannot review your own asset." },
        { ErrorCodes.ERR_REVIEW_CREATE_FAILED, "Failed to create review." },
        { ErrorCodes.ERR_NOT_FOUND, "Resource not found." },
        { ErrorCodes.ERR_FORBIDDEN, "Forbidden." },
        { ErrorCodes.ERR_BAD_REQUEST, "An error occurred." }
    };

    public static string GetMessage(string code) =>
        _map.GetValueOrDefault(code, "An error occurred.");
}
