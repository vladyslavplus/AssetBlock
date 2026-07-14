using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.HostedServices;
using AssetBlock.Infrastructure.Options;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using Elastic.Clients.Elasticsearch;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;
using StackExchange.Redis;

namespace AssetBlock.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

        services.AddOptions<MinioOptions>()
            .Bind(configuration.GetSection(MinioOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<MinioOptions>, MinioOptionsValidator>();

        services.AddOptions<EncryptionOptions>()
            .Bind(configuration.GetSection(EncryptionOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EncryptionOptions>, EncryptionOptionsValidator>();

        services.AddOptions<StripeOptions>()
            .Bind(configuration.GetSection(StripeOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StripeOptions>, StripeOptionsValidator>();

        services.AddOptions<ElasticsearchOptions>()
            .Bind(configuration.GetSection(ElasticsearchOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ElasticsearchOptions>, ElasticsearchOptionsValidator>();
        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddHostedService<DatabaseMigrationService>();
        services.AddHostedService<MinioBucketEnsureHostedService>();
        services.AddHostedService<OutboxDispatcher>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IOutboxStore, OutboxStore>();
        services.AddScoped<IOutboxMessageHandler, AssetIndexUpsertOutboxHandler>();
        services.AddScoped<IOutboxMessageHandler, AssetIndexDeleteOutboxHandler>();
        services.AddScoped<IOutboxMessageHandler, AssetBlobDeleteOutboxHandler>();
        services.AddScoped<IOutboxMessageHandler, PurchaseCompletedOutboxHandler>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IUserStore, UserStore>();
        services.AddScoped<ICategoryStore, CategoryStore>();
        services.AddScoped<IAssetStore, AssetStore>();
        services.AddScoped<IPurchaseStore, PurchaseStore>();
        services.AddScoped<IReviewStore, ReviewStore>();
        services.AddScoped<ISocialPlatformStore, SocialPlatformStore>();
        services.AddScoped<INotificationStore, NotificationStore>();
        services.AddScoped<ITagStore, TagStore>();
        services.AddScoped<IPaymentService, StripePaymentService>();
        services.AddScoped<IDownloadService, DownloadService>();
        services.AddScoped<IAssetStorageService, MinioAssetStorageService>();
        services.AddSingleton<IEncryptionService, AesGcmEncryptionService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();

        var redisConfiguration = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrEmpty(redisConfiguration))
        {
            services.AddSingleton<IConnectionMultiplexer>(_ =>
            {
                var opts = ConfigurationOptions.Parse(redisConfiguration);
                opts.AbortOnConnectFail = false;
                opts.ConnectTimeout = 5000;
                return ConnectionMultiplexer.Connect(opts);
            });
            services.AddSingleton<ICacheService, RedisCacheService>();
        }
        else
        {
            services.AddMemoryCache();
            services.AddSingleton<ICacheService, MemoryCacheService>();
        }

        // Search (Elasticsearch)
        services.AddSingleton(sp =>
        {
            var opts = sp.GetRequiredService<IOptions<ElasticsearchOptions>>().Value;
            var settings = new ElasticsearchClientSettings(new Uri(opts.Url))
                .DefaultIndex(opts.DefaultIndex);
            return new ElasticsearchClient(settings);
        });
        services.AddScoped<IAssetSearchService, Search.ElasticSearchService>();

        // Polly v8 resilience pipelines for external services
        services.AddResiliencePipeline(ResilienceConstants.Pipelines.STRIPE, builder =>
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = ResilienceConstants.Stripe.MAX_RETRIES,
                Delay = TimeSpan.FromMilliseconds(ResilienceConstants.Stripe.RETRY_DELAY_MS),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true
            });
            builder.AddCircuitBreaker(new Polly.CircuitBreaker.CircuitBreakerStrategyOptions
            {
                FailureRatio = ResilienceConstants.Stripe.FAILURE_RATIO,
                SamplingDuration = TimeSpan.FromSeconds(ResilienceConstants.Stripe.SAMPLING_DURATION_SECONDS),
                MinimumThroughput = ResilienceConstants.Stripe.MIN_THROUGHPUT,
                BreakDuration = TimeSpan.FromSeconds(ResilienceConstants.Stripe.BREAK_DURATION_SECONDS)
            });
            builder.AddTimeout(TimeSpan.FromSeconds(ResilienceConstants.Stripe.TIMEOUT_SECONDS));
        });

        services.AddResiliencePipeline(ResilienceConstants.Pipelines.MINIO, builder =>
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = ResilienceConstants.Minio.MAX_RETRIES,
                Delay = TimeSpan.FromMilliseconds(ResilienceConstants.Minio.RETRY_DELAY_MS),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true
            });
            builder.AddTimeout(TimeSpan.FromSeconds(ResilienceConstants.Minio.TIMEOUT_SECONDS));
        });

        services.AddResiliencePipeline(ResilienceConstants.Pipelines.ELASTICSEARCH, builder =>
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = ResilienceConstants.Elasticsearch.MAX_RETRIES,
                Delay = TimeSpan.FromMilliseconds(ResilienceConstants.Elasticsearch.RETRY_DELAY_MS),
                BackoffType = DelayBackoffType.Exponential,
                UseJitter = true
            });
            builder.AddTimeout(TimeSpan.FromSeconds(ResilienceConstants.Elasticsearch.TIMEOUT_SECONDS));
        });

        return services;
    }
}
