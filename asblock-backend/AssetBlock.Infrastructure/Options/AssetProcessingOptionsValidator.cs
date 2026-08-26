using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Options;

internal sealed class AssetProcessingOptionsValidator : IValidateOptions<AssetProcessingOptions>
{
    public ValidateOptionsResult Validate(string? name, AssetProcessingOptions options)
    {
        var errors = new List<string>();

        if (options.PollInterval < TimeSpan.FromSeconds(1) || options.PollInterval > TimeSpan.FromMinutes(5))
        {
            errors.Add("PollInterval must be between 1 second and 5 minutes.");
        }

        if (options.BatchSize < AssetProcessingOptions.MIN_BATCH_SIZE || options.BatchSize > AssetProcessingOptions.MAX_BATCH_SIZE)
        {
            errors.Add($"BatchSize must be between {AssetProcessingOptions.MIN_BATCH_SIZE} and {AssetProcessingOptions.MAX_BATCH_SIZE}.");
        }

        if (options.Concurrency < AssetProcessingOptions.MIN_CONCURRENCY || options.Concurrency > AssetProcessingOptions.MAX_CONCURRENCY)
        {
            errors.Add($"Concurrency must be between {AssetProcessingOptions.MIN_CONCURRENCY} and {AssetProcessingOptions.MAX_CONCURRENCY}.");
        }

        if (options.Concurrency > options.BatchSize)
        {
            errors.Add("Concurrency cannot be greater than BatchSize.");
        }

        if (options.LeaseDuration < TimeSpan.FromSeconds(30) || options.LeaseDuration > TimeSpan.FromHours(1))
        {
            errors.Add("LeaseDuration must be between 30 seconds and 1 hour.");
        }

        if (options.OperationTimeout < TimeSpan.FromSeconds(10) || options.OperationTimeout > TimeSpan.FromHours(1))
        {
            errors.Add("OperationTimeout must be between 10 seconds and 1 hour.");
        }

        if (options.LeaseDuration < options.OperationTimeout + TimeSpan.FromSeconds(30))
        {
            errors.Add("LeaseDuration must be at least 30 seconds greater than OperationTimeout.");
        }

        if (options.MaxAttempts < AssetProcessingOptions.MIN_MAX_ATTEMPTS || options.MaxAttempts > AssetProcessingOptions.MAX_MAX_ATTEMPTS)
        {
            errors.Add($"MaxAttempts must be between {AssetProcessingOptions.MIN_MAX_ATTEMPTS} and {AssetProcessingOptions.MAX_MAX_ATTEMPTS}.");
        }

        if (options.InitialRetryDelay < TimeSpan.FromSeconds(5) || options.InitialRetryDelay > TimeSpan.FromMinutes(10))
        {
            errors.Add("InitialRetryDelay must be between 5 seconds and 10 minutes.");
        }

        if (options.MaxRetryDelay < TimeSpan.FromMinutes(1) || options.MaxRetryDelay > TimeSpan.FromHours(24))
        {
            errors.Add("MaxRetryDelay must be between 1 minute and 24 hours.");
        }

        if (options.InitialRetryDelay > options.MaxRetryDelay)
        {
            errors.Add("InitialRetryDelay cannot be greater than MaxRetryDelay.");
        }

        return errors.Count > 0
            ? ValidateOptionsResult.Fail(errors)
            : ValidateOptionsResult.Success;
    }
}
