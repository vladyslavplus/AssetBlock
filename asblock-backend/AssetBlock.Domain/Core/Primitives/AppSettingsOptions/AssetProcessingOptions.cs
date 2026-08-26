namespace AssetBlock.Domain.Core.Primitives.AppSettingsOptions;

public sealed class AssetProcessingOptions
{
    public const string SECTION_NAME = "AssetProcessing";

    public const int MIN_BATCH_SIZE = 1;
    public const int MAX_BATCH_SIZE = 100;
    public const int MIN_CONCURRENCY = 1;
    public const int MAX_CONCURRENCY = 200;
    public const int MIN_MAX_ATTEMPTS = 1;
    public const int MAX_MAX_ATTEMPTS = 10;
    
    public bool Enabled { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(10);
    public int BatchSize { get; init; } = 10;
    public int Concurrency { get; init; } = 10;
    public TimeSpan LeaseDuration { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMinutes(4);
    public int MaxAttempts { get; init; } = 3;
    public TimeSpan InitialRetryDelay { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan MaxRetryDelay { get; init; } = TimeSpan.FromHours(1);
}
