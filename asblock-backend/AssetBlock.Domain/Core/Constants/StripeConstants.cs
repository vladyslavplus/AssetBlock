namespace AssetBlock.Domain.Core.Constants;

public static class StripeConstants
{
    public const string CURRENCY_USD = "usd";
    public const string MODE_PAYMENT = "payment";

    public const string PAYMENT_STATUS_PAID = "paid";

    public static class MetadataKeys
    {
        public const string USER_ID = "userId";
        public const string ASSET_ID = "assetId";
    }

    public static class Events
    {
        public const string CHECKOUT_SESSION_COMPLETED = "checkout.session.completed";
    }
}
