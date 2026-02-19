namespace AssetBlock.Domain.Core.Constants;

/// <summary>
/// Rate limiting policy names. Use with [EnableRateLimiting(RateLimitingConstants.Policies.xxx)].
/// </summary>
public static class RateLimitingConstants
{
    public static class Policies
    {
        public const string AUTH_REGISTER = "auth-register";
        public const string AUTH_LOGIN = "auth-login";
        public const string ASSETS_UPLOAD = "assets-upload";
        public const string ASSETS_DOWNLOAD = "assets-download";
        public const string PAYMENTS_CHECKOUT = "payments-checkout";
    }

    public static class Windows
    {
        public const int SLIDING_WINDOW_SEGMENTS = 6;

        public const int AUTH_REGISTER_LIMIT = 5;
        public const int AUTH_REGISTER_PERIOD_SECONDS = 60;

        public const int AUTH_LOGIN_LIMIT = 10;
        public const int AUTH_LOGIN_PERIOD_SECONDS = 60;

        public const int ASSETS_UPLOAD_LIMIT = 10;
        public const int ASSETS_UPLOAD_PERIOD_SECONDS = 3600;

        public const int ASSETS_DOWNLOAD_LIMIT = 30;
        public const int ASSETS_DOWNLOAD_PERIOD_SECONDS = 60;

        public const int PAYMENTS_CHECKOUT_LIMIT = 10;
        public const int PAYMENTS_CHECKOUT_PERIOD_SECONDS = 60;
    }
}
