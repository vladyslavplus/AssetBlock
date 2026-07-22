namespace AssetBlock.Domain.Core.Constants;

public static class StripeConstants
{
    public const string CURRENCY_USD = "usd";
    public const string MODE_PAYMENT = "payment";

    public const string PAYMENT_STATUS_PAID = "paid";

    public static class CheckoutSessionStatuses
    {
        public const string OPEN = "open";
        public const string COMPLETE = "complete";
        public const string EXPIRED = "expired";
    }

    public static class MetadataKeys
    {
        public const string USER_ID = "userId";
        public const string ASSET_ID = "assetId";
        public const string ASSET_VERSION_ID = "assetVersionId";
        public const string CHECKOUT_INTENT_ID = "checkoutIntentId";
    }

    public static class Events
    {
        public const string CHECKOUT_SESSION_COMPLETED = "checkout.session.completed";
    }
}
