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
        public const string ELASTICSEARCH = "elasticsearch";
    }

    public static class Stripe
    {
        public const int MAX_RETRIES = 3;
        public const int RETRY_DELAY_MS = 500;
        public const int TIMEOUT_SECONDS = 30;
        public const double FAILURE_RATIO = 0.5;
        public const int SAMPLING_DURATION_SECONDS = 30;
        public const int MIN_THROUGHPUT = 5;
        public const int BREAK_DURATION_SECONDS = 30;
    }

    public static class Minio
    {
        public const int MAX_RETRIES = 2;
        public const int RETRY_DELAY_MS = 300;
        public const int TIMEOUT_SECONDS = 60;
    }

    public static class Elasticsearch
    {
        public const int MAX_RETRIES = 3;
        public const int RETRY_DELAY_MS = 500;
        public const int TIMEOUT_SECONDS = 30;
    }
}
