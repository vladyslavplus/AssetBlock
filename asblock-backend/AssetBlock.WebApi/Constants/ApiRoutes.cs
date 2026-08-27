namespace AssetBlock.WebApi.Constants;

/// <summary>
/// API route segments. Use with [Route], [HttpPost(Routes.X)], etc.
/// </summary>
public static class ApiRoutes
{
    public static class Auth
    {
        public const string LOGIN = "login";
        public const string REFRESH = "refresh";
        public const string LOGOUT = "logout";
        public const string REGISTER = "register";
        public const string PASSWORD_RESET_REQUEST = "password-reset/request";
        public const string PASSWORD_RESET_CONFIRM = "password-reset/confirm";
        public const string EMAIL_VERIFICATION_CONFIRM = "email-verification/confirm";
        public const string EMAIL_CHANGE_CONFIRM = "email-change/confirm";
    }

    public static class Categories
    {
        public const string LIST = "";
        public const string BY_ID = "{id:guid}";
    }

    public static class Assets
    {
        public const string LIST = "";
        public const string UPLOAD = "upload";
        public const string ID = "{id:guid}";
        public const string DOWNLOAD = "{id:guid}/download";
        public const string TAGS = "{id:guid}/tags";
        public const string TAGS_ID = "{id:guid}/tags/{tagId:guid}";
        public const string VERSIONS = "{id:guid}/versions";
        public const string VERSION_PUBLISH = "{id:guid}/versions";
        public const string VERSION_DOWNLOAD = "{id:guid}/versions/{versionId:guid}/download";
    }

    public static class Payments
    {
        public const string CAPABILITIES = "capabilities";
        public const string CHECKOUT = "checkout";
        public const string CHECKOUT_BUNDLES = "checkout/bundles";
        public const string CHECKOUT_STATUS = "checkout/{checkoutIntentId:guid}/status";
        public const string WEBHOOK = "webhook";
    }

    public static class Bundles
    {
        public const string LIST = "";
        public const string BY_ID = "{id:guid}";
    }

    public static class SellerBundles
    {
        public const string BASE = "api/seller/bundles";
        public const string LIST = "";
        public const string BY_ID = "{id:guid}";
        public const string ARCHIVE = "{id:guid}/archive";
        public const string RESTORE = "{id:guid}/restore";
    }

    public static class Collections
    {
        public const string LIST = "";
        public const string BY_ID = "{id:guid}";
    }

    public static class SellerCollections
    {
        public const string BASE = "api/seller/collections";
        public const string LIST = "";
        public const string BY_ID = "{id:guid}";
        public const string ITEMS = "{id:guid}/items";
        public const string ITEM_BY_ASSET = "{id:guid}/items/{assetId:guid}";
        public const string ITEMS_ORDER = "{id:guid}/items/order";
        public const string PUBLISH = "{id:guid}/publish";
        public const string ARCHIVE = "{id:guid}/archive";
        public const string RESTORE = "{id:guid}/restore";
    }

    public static class Reviews
    {
        public const string LIST_FOR_ASSET = "assets/{assetId:guid}/reviews";
        public const string CREATE_FOR_ASSET = "assets/{assetId:guid}/reviews";
        public const string BY_ID = "{id:guid}";
    }

    public static class Tags
    {
        public const string BASE = "";
        public const string ID = "{id:guid}";
    }

    public static class Users
    {
        public const string SOCIAL_PLATFORMS = "social-platforms";
        public const string PROFILE = "{username}";
        public const string ME = "me";
        public const string ME_PASSWORD = "me/password";
        public const string ME_EMAIL_VERIFICATION_RESEND = "me/email-verification/resend";
        public const string ME_EMAIL_CHANGE_REQUEST = "me/email-change/request";
        public const string ME_SOCIALS = "me/socials";
        public const string ME_NOTIFICATIONS = "me/notifications";
        public const string ME_NOTIFICATIONS_READ_ALL = "me/notifications/read-all";
        public const string ME_NOTIFICATION_READ = "me/notifications/{id:guid}/read";
        public const string ME_NOTIFICATION_UNREAD = "me/notifications/{id:guid}/unread";
        public const string ME_PURCHASES = "me/purchases";
        public const string ME_ASSETS = "me/assets";
        public const string ME_ASSET = "me/assets/{assetId:guid}";
        public const string ME_ASSET_PROCESSING_JOBS = "me/assets/{assetId:guid}/processing-jobs";
        public const string ME_ASSET_VERSION_PROCESSING_JOBS = "me/asset-versions/{assetVersionId:guid}/processing-jobs";
        public const string ME_ASSET_VERSION_LISTING_COPILOT = "me/asset-versions/{assetVersionId:guid}/listing-copilot";
    }

    public static class Analytics
    {
        public const string BASE = "api/analytics";
        public const string EVENTS = "events";
    }

    public static class SellerAnalytics
    {
        public const string BASE = "api/seller/analytics";
        public const string OVERVIEW = "overview";
        public const string PRODUCTS = "products";
        public const string PRODUCT_ASSET_BY_ID = "products/assets/{id:guid}";
        public const string PRODUCT_BUNDLE_BY_ID = "products/bundles/{id:guid}";
        public const string COLLECTIONS = "collections";
        public const string SALES = "sales";
        public const string SALES_EXPORT = "sales/export";
    }

    public static class Admin
    {
        public const string AUDIT_LOGS = "api/admin/audit-logs";
    }

    public static class Hubs
    {
        // Keep absolute PathString format; used by MapHub and JWT OnMessageReceived StartsWithSegments check.
        public const string NOTIFICATIONS = "/hubs/notifications";
    }
}
