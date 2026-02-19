namespace AssetBlock.Infrastructure.Services;

/// <summary>
/// Polly resilience pipeline keys and configuration constants.
/// </summary>
internal static class ResilienceConstants
{
    public static class Pipelines
    {
        public const string STRIPE = "stripe";
        public const string MINIO = "minio";
    }

    public static class Stripe
    {
        public const int MAX_RETRIES = 3;
        public const int RETRY_DELAY_MS = 500;
        public const int TIMEOUT_SECONDS = 30;
    }

    public static class Minio
    {
        public const int MAX_RETRIES = 2;
        public const int RETRY_DELAY_MS = 300;
        public const int TIMEOUT_SECONDS = 60;
    }
}
