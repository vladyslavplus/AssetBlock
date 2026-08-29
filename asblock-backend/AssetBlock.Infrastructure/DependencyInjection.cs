using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Ai;
using AssetBlock.Infrastructure.Email;
using AssetBlock.Infrastructure.HostedServices;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing.Handlers;
using AssetBlock.Infrastructure.Options;
using AssetBlock.Infrastructure.Outbox;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Polly;
using Polly.Retry;

namespace AssetBlock.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("Connection string 'DefaultConnection' not found.");

        services.TryAddSingleton(configuration);
        services.TryAddSingleton(environment);

        services.AddOptions<DatabaseOptions>()
            .Bind(configuration.GetSection(DatabaseOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DatabaseOptions>, DatabaseOptionsValidator>();

        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<JwtOptions>, JwtOptionsValidator>();

        services.AddOptions<EncryptionOptions>()
            .Bind(configuration.GetSection(EncryptionOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EncryptionOptions>, EncryptionOptionsValidator>();

        services.AddOptions<StripeOptions>()
            .Bind(configuration.GetSection(StripeOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<StripeOptions>, StripeOptionsValidator>();

        services.AddOptions<FileUploadOptions>()
            .Bind(configuration.GetSection(FileUploadOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<FileUploadOptions>, FileUploadOptionsValidator>();

        services.AddOptions<EmailOptions>()
            .Bind(configuration.GetSection(EmailOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>();

        services.AddOptions<DataProtectionOptions>()
            .Bind(configuration.GetSection(DataProtectionOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<DataProtectionOptions>, DataProtectionOptionsValidator>();

        services.AddOptions<ObservabilityOptions>()
            .Bind(configuration.GetSection(ObservabilityOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ObservabilityOptions>, ObservabilityOptionsValidator>();

        services.AddOptions<AssetProcessingOptions>()
            .Bind(configuration.GetSection(AssetProcessingOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AssetProcessingOptions>, AssetProcessingOptionsValidator>();

        services.TryAddSingleton(TimeProvider.System);
        services.AddAnalyticsDistributedRateLimiting(configuration, environment);
        services.AddAnalyticsAggregationOptions(configuration);

        services.AddDbContext<ApplicationDbContext>(options =>
            options.UseNpgsql(connectionString));
        services.AddDbContextFactory<ApplicationDbContext>(
            options => options.UseNpgsql(connectionString),
            ServiceLifetime.Scoped);
        services.AddHostedService<DatabaseMigrationService>();
        services.AddHostedService<OutboxDispatcher>();
        services.AddOptions<ArchiveInspectionOptions>()
            .Bind(configuration.GetSection(ArchiveInspectionOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ArchiveInspectionOptions>, ArchiveInspectionOptionsValidator>();

        services.AddOptions<ClamAvOptions>()
            .Bind(configuration.GetSection(ClamAvOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<ClamAvOptions>, ClamAvOptionsValidator>();

        services.AddSingleton<IAiTelemetry, AiTelemetry>();
        services.AddOptions<AiOptions>()
            .Bind(configuration.GetSection(AiOptions.SECTION_NAME))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<AiOptions>, AiOptionsValidator>();
        services.AddOptions<OpenRouterOptions>()
            .Bind(configuration.GetSection(OpenRouterOptions.CONFIGURATION_PATH))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OpenRouterOptions>, OpenRouterOptionsValidator>();
        services.AddOptions<OllamaOptions>()
            .Bind(configuration.GetSection(OllamaOptions.CONFIGURATION_PATH))
            .ValidateOnStart();
        services.AddSingleton<IValidateOptions<OllamaOptions>, OllamaOptionsValidator>();
        services.AddHttpClient(OpenRouterAiGenerationProvider.HTTP_CLIENT_NAME, (sp, client) =>
        {
            OpenRouterOptions options = sp.GetRequiredService<IOptions<OpenRouterOptions>>().Value;
            client.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddHttpClient(OllamaAiGenerationProvider.HTTP_CLIENT_NAME, (sp, client) =>
        {
            OllamaOptions options = sp.GetRequiredService<IOptions<OllamaOptions>>().Value;
            client.BaseAddress = new Uri(EnsureTrailingSlash(options.BaseUrl));
            client.Timeout = Timeout.InfiniteTimeSpan;
        });
        services.AddSingleton<IAiGenerationProvider, OpenRouterAiGenerationProvider>();
        services.AddSingleton<IAiGenerationProvider, OllamaAiGenerationProvider>();
        services.AddSingleton<IAiGenerationProviderRegistry, AiGenerationProviderRegistry>();

        services.AddHostedService<StorageOrphanCleanupWorker>();
        services.AddHostedService<CheckoutReservationCleanupWorker>();
        services.AddHostedService<RefreshTokenRetentionWorker>();
        services.AddHostedService<AnalyticsAggregationWorker>();
        services.AddHostedService<AssetProcessingWorker>();
        services.AddSingleton<IAssetProcessingJobRegistry, AssetProcessingJobRegistry>();
        services.AddSingleton<IAssetProcessingJobHandlerAdapter, AssetProcessingJobHandlerAdapter<ArchiveInspectionJobHandler, ArchiveInspectionPayload, ArchiveInspectionResult>>(
            _ => new AssetProcessingJobHandlerAdapter<ArchiveInspectionJobHandler, ArchiveInspectionPayload, ArchiveInspectionResult>(AssetProcessingJobType.ARCHIVE_INSPECTION));
        services.AddSingleton<IAssetProcessingJobHandlerAdapter, AssetProcessingJobHandlerAdapter<MalwareScanJobHandler, MalwareScanPayload, MalwareScanResult>>(
            _ => new AssetProcessingJobHandlerAdapter<MalwareScanJobHandler, MalwareScanPayload, MalwareScanResult>(AssetProcessingJobType.MALWARE_SCAN));
        services.AddScoped<ArchiveInspectionJobHandler>();
        services.AddScoped<MalwareScanJobHandler>();
        services.AddAssetProcessingJobHandler<ListingCopilotJobHandler, ListingCopilotPayload, ListingCopilotResult>(
            AssetProcessingJobType.LISTING_COPILOT);
        services.AddSingleton<IArchiveSafetyInspector, ArchiveSafetyInspector>();
        services.AddSingleton<IContentMalwareScanner, ClamAvContentMalwareScanner>();
        services.AddScoped<IUnitOfWork, EfUnitOfWork>();
        services.AddScoped<IOutboxStore, OutboxStore>();
        services.AddScoped<IEmailDeliveryStore, EmailDeliveryStore>();
        services.AddScoped<IAssetProcessingJobStore, AssetProcessingJobStore>();
        services.AddScoped<IOutboxMessageHandler, AssetBlobDeleteOutboxHandler>();
        services.AddScoped<IOutboxMessageHandler, OrderCompletedOutboxHandler>();
        services.AddScoped<IOutboxMessageHandler, EmailDispatchOutboxHandler>();
        services.AddScoped<IOutboxMessageHandler, EmailActionDispatchOutboxHandler>();
        services.AddScoped<IEmailSender, SmtpEmailSender>();
        services.AddSingleton<IEmailActionLinkProtector, EmailActionLinkProtector>();
        services.AddScoped<IEmailActionStore, EmailActionStore>();
        services.AddScoped<IJwtTokenService, JwtTokenService>();
        services.AddScoped<IUserStore, UserStore>();
        services.AddScoped<IUserVerificationStore, UserVerificationStore>();

        services.AddScoped<ICategoryStore, CategoryStore>();
        services.AddScoped<IAssetStore, AssetStore>();
        services.AddScoped<IAssetArchiveAnalysisStore, AssetArchiveAnalysisStore>();
        services.AddScoped<IListingCopilotStore, ListingCopilotStore>();
        services.AddScoped<IAssetProcessingLifecycleStore, AssetProcessingLifecycleStore>();
        services.AddScoped<IPurchaseStore, PurchaseStore>();
        services.AddScoped<ICheckoutIntentStore, CheckoutIntentStore>();
        services.AddScoped<ICollectionStore, CollectionStore>();
        services.AddScoped<IBundleStore, BundleStore>();
        services.AddScoped<IOrderStore, OrderStore>();
        services.AddScoped<IReviewStore, ReviewStore>();
        services.AddScoped<ISocialPlatformStore, SocialPlatformStore>();
        services.AddScoped<INotificationStore, NotificationStore>();
        services.AddScoped<ITagStore, TagStore>();
        services.AddScoped<ISellerAnalyticsStore, SellerAnalyticsStore>();
        services.AddScoped<IAnalyticsEventStore, AnalyticsEventStore>();
        services.AddScoped<IAuditStore, AuditStore>();
        services.AddScoped<IAuditWriter, AuditWriter>();
        services.AddScoped<IAuditContextAccessor, NullAuditContextAccessor>();
        services.AddScoped<IPaymentService, StripePaymentService>();
        services.AddScoped<IDownloadService, DownloadService>();
        services.AddAssetStorage(configuration);
        services.AddSingleton<IEncryptionService, AesGcmEncryptionService>();
        services.AddSingleton<IPasswordHasher, PasswordHasher>();

        var redisConfiguration = configuration.GetConnectionString("Redis");
        if (!string.IsNullOrWhiteSpace(redisConfiguration))
        {
            services.AddRedisConnectionMultiplexer(redisConfiguration);
            services.AddSingleton<ICacheService, RedisCacheService>();
        }
        else
        {
            services.AddMemoryCache();
            services.AddSingleton<ICacheService, MemoryCacheService>();
        }

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

        return services;
    }

    private static string EnsureTrailingSlash(string baseUrl) =>
        baseUrl.EndsWith('/') ? baseUrl : baseUrl + "/";

    private static IServiceCollection AddAssetProcessingJobHandler<THandler, TPayload, TResult>(
        this IServiceCollection services,
        AssetProcessingJobType jobType)
        where THandler : class, IAssetProcessingJobHandler<TPayload, TResult>
        where TPayload : AssetProcessingPayload
        where TResult : AssetProcessingResult
    {
        services.AddScoped<THandler>();
        services.AddSingleton<IAssetProcessingJobHandlerAdapter>(
            new AssetProcessingJobHandlerAdapter<THandler, TPayload, TResult>(jobType));
        return services;
    }
}
