using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Domain.Core.Primitives.Storage;
using AssetBlock.Infrastructure.HostedServices;
using AssetBlock.Infrastructure.Options;
using AssetBlock.Infrastructure.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using Polly;
using Polly.Registry;
using Polly.Retry;

namespace AssetBlock.Infrastructure;

internal static class AssetStorageServiceCollectionExtensions
{
    public static IServiceCollection AddAssetStorage(this IServiceCollection services, IConfiguration configuration)
    {
        // Host already registers IConfiguration; unit tests that call AddInfrastructure directly need it for provider-gated validators.
        services.TryAddSingleton(configuration);

        services.AddOptions<StorageOptions>()
            .Bind(configuration.GetSection(StorageOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StorageOptions>, StorageOptionsValidator>();

        services.AddOptions<MinioOptions>()
            .Bind(configuration.GetSection(MinioOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MinioOptions>, MinioOptionsValidator>();

        services.AddOptions<SeaweedFsOptions>()
            .Bind(configuration.GetSection(SeaweedFsOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<SeaweedFsOptions>, SeaweedFsOptionsValidator>();

        var providerRaw = configuration.GetSection(StorageOptions.SECTION_NAME)["Provider"];
        if (!StorageProvider.TryParse(providerRaw, out var provider))
        {
            // StorageOptionsValidator fails at ValidateOnStart; still fail fast if DI runs earlier.
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(providerRaw)
                    ? "Storage:Provider is required. Supported values: SeaweedFs, Minio."
                    : $"Storage:Provider '{providerRaw}' is unknown. Supported values: SeaweedFs, Minio.");
        }

        services.AddSingleton<IMinioClient>(sp =>
        {
            if (provider == StorageProvider.MINIO)
            {
                MinioOptions opts = sp.GetRequiredService<IOptions<MinioOptions>>().Value;
                return S3CompatibleClientFactory.Create(opts.Endpoint, opts.AccessKey, opts.SecretKey, opts.UseSsl);
            }

            SeaweedFsOptions seaweed = sp.GetRequiredService<IOptions<SeaweedFsOptions>>().Value;
            return S3CompatibleClientFactory.Create(
                seaweed.Endpoint,
                seaweed.AccessKey,
                seaweed.SecretKey,
                seaweed.UseSsl);
        });

        services.AddScoped<IAssetStorageService>(sp =>
        {
            IMinioClient client = sp.GetRequiredService<IMinioClient>();
            ResiliencePipelineProvider<string> resilience = sp.GetRequiredService<ResiliencePipelineProvider<string>>();
            ILogger<S3CompatibleAssetStorageService> logger = sp.GetRequiredService<ILogger<S3CompatibleAssetStorageService>>();

            var bucket = provider == StorageProvider.MINIO
                ? sp.GetRequiredService<IOptions<MinioOptions>>().Value.Bucket
                : sp.GetRequiredService<IOptions<SeaweedFsOptions>>().Value.Bucket;

            return new S3CompatibleAssetStorageService(client, bucket, resilience, logger);
        });

        services.AddHostedService<StorageBucketEnsureHostedService>();

        // Delete/list operations can safely be replayed after a transient failure.
        services.AddResiliencePipeline(ResilienceConstants.Pipelines.STORAGE_REPLAYABLE, builder =>
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = ResilienceConstants.Storage.MAX_RETRIES,
                Delay = TimeSpan.FromMilliseconds(ResilienceConstants.Storage.RETRY_DELAY_MS),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true
            });
            builder.AddTimeout(TimeSpan.FromSeconds(ResilienceConstants.Storage.TIMEOUT_SECONDS));
        });

        // Upload and download bodies are one-shot streams. Retrying them would send a partial
        // upload or append a second plaintext sequence to an already-started HTTP response.
        services.AddResiliencePipeline(ResilienceConstants.Pipelines.STORAGE_STREAMING, builder =>
        {
            builder.AddTimeout(TimeSpan.FromSeconds(ResilienceConstants.Storage.TIMEOUT_SECONDS));
        });

        return services;
    }
}
