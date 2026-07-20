namespace AssetBlock.Domain.Core.Constants;

/// <summary>Stable machine-readable audit action names.</summary>
public static class AuditActions
{
    public const string AUTH_REGISTER = "Auth.Register";
    public const string AUTH_LOGIN = "Auth.Login";
    public const string AUTH_REFRESH_TOKEN = "Auth.RefreshToken";
    public const string AUTH_EMAIL_VERIFICATION = "Auth.EmailVerification";
    public const string AUTH_PASSWORD_RESET_REQUEST = "Auth.PasswordResetRequest";
    public const string AUTH_PASSWORD_RESET_CONFIRM = "Auth.PasswordResetConfirm";
    public const string AUTH_EMAIL_CHANGE_REQUEST = "Auth.EmailChangeRequest";
    public const string AUTH_EMAIL_CHANGE_CONFIRM = "Auth.EmailChangeConfirm";

    public const string USER_PASSWORD_CHANGE = "User.PasswordChange";
    public const string USER_PROFILE_UPDATE = "User.ProfileUpdate";
    public const string USER_SOCIAL_LINKS_UPDATE = "User.SocialLinksUpdate";

    public const string ASSET_CREATE = "Asset.Create";
    public const string ASSET_UPDATE = "Asset.Update";
    public const string ASSET_DELETE = "Asset.Delete";
    public const string ASSET_SOFT_DELETE = "Asset.SoftDelete";
    public const string ASSET_HARD_DELETE = "Asset.HardDelete";
    public const string ASSET_TAG_ADD = "Asset.TagAdd";
    public const string ASSET_TAG_REMOVE = "Asset.TagRemove";
    public const string ASSET_VERSION_PUBLISH = "Asset.VersionPublish";

    public const string CATEGORY_CREATE = "Category.Create";
    public const string CATEGORY_UPDATE = "Category.Update";
    public const string CATEGORY_DELETE = "Category.Delete";

    public const string TAG_CREATE = "Tag.Create";
    public const string TAG_UPDATE = "Tag.Update";
    public const string TAG_DELETE = "Tag.Delete";

    public const string REVIEW_CREATE = "Review.Create";
    public const string REVIEW_DELETE = "Review.Delete";

    public const string PAYMENT_PURCHASE_COMPLETED = "Payment.PurchaseCompleted";
}
