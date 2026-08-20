using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class AnalyticsAggregationOptionsValidator : IValidateOptions<AnalyticsAggregationOptions>
{
    private const int MIN_INTERVAL_SECONDS = 30;
    private const int MAX_INTERVAL_SECONDS = 3600;
    private const int MIN_RETENTION_BATCH_SIZE = 100;
    private const int MAX_RETENTION_BATCH_SIZE = 50_000;
    private const int MIN_MAX_RETENTION_BATCHES_PER_RUN = 1;
    private const int MAX_MAX_RETENTION_BATCHES_PER_RUN = 100;
    private const int MIN_COMMAND_TIMEOUT_SECONDS = 10;
    private const int MAX_COMMAND_TIMEOUT_SECONDS = 600;

    public ValidateOptionsResult Validate(string? name, AnalyticsAggregationOptions options)
    {
        if (options.IntervalSeconds is < MIN_INTERVAL_SECONDS or > MAX_INTERVAL_SECONDS)
        {
            return ValidateOptionsResult.Fail(
                $"AnalyticsAggregation:IntervalSeconds must be between {MIN_INTERVAL_SECONDS} and {MAX_INTERVAL_SECONDS}.");
        }

        if (options.RetentionBatchSize is < MIN_RETENTION_BATCH_SIZE or > MAX_RETENTION_BATCH_SIZE)
        {
            return ValidateOptionsResult.Fail(
                $"AnalyticsAggregation:RetentionBatchSize must be between {MIN_RETENTION_BATCH_SIZE} and {MAX_RETENTION_BATCH_SIZE}.");
        }

        if (options.MaxRetentionBatchesPerRun is < MIN_MAX_RETENTION_BATCHES_PER_RUN or > MAX_MAX_RETENTION_BATCHES_PER_RUN)
        {
            return ValidateOptionsResult.Fail(
                $"AnalyticsAggregation:MaxRetentionBatchesPerRun must be between {MIN_MAX_RETENTION_BATCHES_PER_RUN} and {MAX_MAX_RETENTION_BATCHES_PER_RUN}.");
        }

        if (options.CommandTimeoutSeconds is < MIN_COMMAND_TIMEOUT_SECONDS or > MAX_COMMAND_TIMEOUT_SECONDS)
        {
            return ValidateOptionsResult.Fail(
                $"AnalyticsAggregation:CommandTimeoutSeconds must be between {MIN_COMMAND_TIMEOUT_SECONDS} and {MAX_COMMAND_TIMEOUT_SECONDS}.");
        }

        return ValidateOptionsResult.Success;
    }
}
