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

    /// <summary>
    /// Stripe Checkout Session metadata. Only checkoutIntentId and userId are written going forward;
    /// product/version/price data is loaded from the durable CheckoutIntent snapshot.
    /// </summary>
    public static class MetadataKeys
    {
        public const string USER_ID = "userId";
        public const string CHECKOUT_INTENT_ID = "checkoutIntentId";
    }

    public static class Events
    {
        public const string CHECKOUT_SESSION_COMPLETED = "checkout.session.completed";
    }
}
