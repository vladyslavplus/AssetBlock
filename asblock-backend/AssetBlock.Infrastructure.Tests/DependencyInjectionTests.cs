using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using AssetBlock.Infrastructure.RateLimiting;
using StackExchange.Redis;

namespace AssetBlock.Infrastructure.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddInfrastructure_resolves_core_services()
    {
        var services = BuildValidServices(new TestHostEnvironment());
        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IJwtTokenService>().Should().NotBeNull();
        sp.GetRequiredService<IUserStore>().Should().NotBeNull();
        sp.GetRequiredService<IUserVerificationStore>().Should().NotBeNull();
        sp.GetRequiredService<ICategoryStore>().Should().NotBeNull();
        sp.GetRequiredService<IAssetStore>().Should().NotBeNull();
        sp.GetRequiredService<IPurchaseStore>().Should().NotBeNull();
        sp.GetRequiredService<IReviewStore>().Should().NotBeNull();
        sp.GetRequiredService<ITagStore>().Should().NotBeNull();
        sp.GetRequiredService<INotificationStore>().Should().NotBeNull();
        sp.GetRequiredService<ISocialPlatformStore>().Should().NotBeNull();
        sp.GetRequiredService<IPaymentService>().Should().NotBeNull();
        sp.GetRequiredService<IDownloadService>().Should().NotBeNull();
        sp.GetRequiredService<IAssetStorageService>().Should().NotBeNull();
        sp.GetRequiredService<IEncryptionService>().Should().NotBeNull();
        sp.GetRequiredService<IPasswordHasher>().Should().NotBeNull();
        sp.GetRequiredService<ICacheService>().Should().NotBeNull();
        sp.GetRequiredService<IEmailSender>().Should().NotBeNull();
        sp.GetRequiredService<IEmailActionStore>().Should().NotBeNull();
        sp.GetRequiredService<IEmailActionLinkProtector>().Should().NotBeNull();
        sp.GetRequiredService<ApplicationDbContext>();
        sp.GetRequiredService<IAiTelemetry>().Should().NotBeNull();
        sp.GetServices<IAiGenerationProvider>().Select(p => p.Kind).Should()
            .BeEquivalentTo([AiProviderKind.OPENROUTER, AiProviderKind.OLLAMA]);
        sp.GetRequiredService<IAiGenerationProviderRegistry>().Should().NotBeNull();
        sp.GetRequiredService<IListingCopilotStore>().Should().NotBeNull();
        sp.GetRequiredService<IOptions<AiOptions>>().Value.Enabled.Should().BeFalse();
        sp.GetRequiredService<IOptions<OpenRouterOptions>>().Value.Models.Should().BeEmpty();
    }

    [Fact]
    public void AddInfrastructure_WhenAiEnabledWithValidOpenRouterModels_ShouldBindOrderedModels()
    {
        var services = BuildValidServices(new TestHostEnvironment(), includeRedis: false, extra: new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:Provider"] = "OpenRouter",
            ["Ai:PromptPolicyVersion"] = "listing-copilot-v1",
            ["Ai:OpenRouter:ApiKey"] = "sk-test-key-value",
            ["Ai:OpenRouter:Models:0"] = "nvidia/nemotron-3-super-120b-a12b:free",
            ["Ai:OpenRouter:Models:1"] = "nex-agi/nex-n2-pro:free"
        });

        using var sp = services.BuildServiceProvider();
        var models = sp.GetRequiredService<IOptions<OpenRouterOptions>>().Value.Models;

        models.Should().Equal(
            "nvidia/nemotron-3-super-120b-a12b:free",
            "nex-agi/nex-n2-pro:free");
    }

    [Fact]
    public void AddInfrastructure_WhenAiEnabledOpenRouterWithEmptyModels_ShouldFailOptionsValidation()
    {
        var services = BuildValidServices(new TestHostEnvironment(), includeRedis: false, extra: new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:Provider"] = "OpenRouter",
            ["Ai:PromptPolicyVersion"] = "listing-copilot-v1",
            ["Ai:OpenRouter:ApiKey"] = "sk-test-key-value"
        });

        using var sp = services.BuildServiceProvider();
        var act = () => _ = sp.GetRequiredService<IOptions<OpenRouterOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddInfrastructure_WhenAiEnabledWithUnknownProvider_ShouldFailOptionsValidation()
    {
        var services = BuildValidServices(new TestHostEnvironment(), includeRedis: false, extra: new Dictionary<string, string?>
        {
            ["Ai:Enabled"] = "true",
            ["Ai:Provider"] = "NotAProvider",
            ["Ai:PromptPolicyVersion"] = "listing-copilot-v1"
        });

        using var sp = services.BuildServiceProvider();
        var act = () => _ = sp.GetRequiredService<IOptions<AiOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddInfrastructure_WhenEncryptionKeyInvalid_ShouldFailOptionsValidation()
    {
        var tempKeysPath = Path.Combine(Path.GetTempPath(), "assetblock-dp-tests", Guid.NewGuid().ToString("N"));
        var json = JsonSerializer.Serialize(new
        {
            ConnectionStrings = new { DefaultConnection = "Host=127.0.0.1;Port=5432;Database=test;Username=u;Password=p" },
            Jwt = new { Key = new string('k', 32), Issuer = "iss", Audience = "aud", AccessTokenMinutes = 15, RefreshTokenDays = 7 },
            Encryption = new { KeyBase64 = "not-valid-base64!!" },
            Storage = new { Provider = "Minio" },
            Minio = new { Endpoint = "http://localhost:9000", Bucket = "assets", AccessKey = "local-access", SecretKey = "local-secret", UseSsl = false },
            SeaweedFs = new { Endpoint = "<seaweedfs-endpoint>:8333", Bucket = "<bucket-name>", AccessKey = "<k>", SecretKey = "<s>", UseSsl = true },
            Stripe = new
            {
                SecretKey = "stripe_test_secret_key_not_real",
                WebhookSecret = "stripe_test_webhook_secret_not_real",
                DefaultSuccessUrl = "http://localhost:3000/payment/success",
                DefaultCancelUrl = "http://localhost:3000/payment/cancel"
            },
            FileUpload = new { MaxFileBytes = 262144000L, AllowedExtensions = new[] { ".zip", ".tar", ".tar.gz", ".tgz" } },
            Email = new
            {
                Provider = "Smtp",
                FromName = "AssetBlock",
                FromAddress = "noreply@localhost",
                PublicAppBaseUrl = "http://localhost:3000",
                MessageIdDomain = "mail.localhost",
                Smtp = new { Host = "localhost", Port = 1025, Security = "NONE", Username = "", Password = "", TimeoutSeconds = 30 }
            },
            DataProtection = new { KeysPath = tempKeysPath },
            AnalyticsRateLimiting = new { BffSigningSecret = new string('s', 32) }
        });
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        Directory.CreateDirectory(tempKeysPath);
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(tempKeysPath));
        services.AddSingleton(Substitute.For<ITransactionalEmailComposer>());
        services.AddInfrastructure(config, new TestHostEnvironment());

        using var sp = services.BuildServiceProvider();
        var act = () => _ = sp.GetRequiredService<IOptions<EncryptionOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void AddAnalyticsDistributedRateLimiting_WhenDevelopmentWithoutRedis_ShouldRegisterInMemoryLimiter()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(includeRedis: false);
        services.AddAnalyticsDistributedRateLimiting(config, new TestHostEnvironment());

        services.Count(d => d.ServiceType == typeof(IAnalyticsDistributedRateLimiter)).Should().Be(1);
        services.Single(d => d.ServiceType == typeof(IAnalyticsDistributedRateLimiter))
            .ImplementationType.Should().Be(typeof(InMemoryAnalyticsDistributedRateLimiter));
        services.Should().NotContain(d => d.ServiceType == typeof(IConnectionMultiplexer));
    }

    [Fact]
    public void AddAnalyticsDistributedRateLimiting_WhenDevelopmentWithRedis_ShouldRegisterRedisLimiterAndSingleMultiplexer()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(includeRedis: true);
        services.AddAnalyticsDistributedRateLimiting(config, new TestHostEnvironment());

        services.Count(d => d.ServiceType == typeof(IConnectionMultiplexer)).Should().Be(1);
        services.Single(d => d.ServiceType == typeof(IAnalyticsDistributedRateLimiter))
            .ImplementationType.Should().Be(typeof(RedisAnalyticsDistributedRateLimiter));
    }

    [Fact]
    public void AddInfrastructure_WhenRedisConfigured_ShouldRegisterSingleMultiplexerForCacheAndLimiter()
    {
        var services = BuildValidServices(new TestHostEnvironment(), includeRedis: true);

        services.Count(d => d.ServiceType == typeof(IConnectionMultiplexer)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IAnalyticsDistributedRateLimiter)).Should().Be(1);
    }

    [Fact]
    public void AddAnalyticsDistributedRateLimiting_WhenProductionWithoutRedis_ShouldThrowOnBuild()
    {
        var services = new ServiceCollection();
        var config = BuildConfig(includeRedis: false);
        var env = new TestHostEnvironment { EnvironmentName = Environments.Production };

        var act = () => services.AddAnalyticsDistributedRateLimiting(config, env);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Redis*");
    }

    [Fact]
    public void AddInfrastructure_WhenCustomTimeProviderRegistered_ShouldNotOverwriteIt()
    {
        var services = new ServiceCollection();
        var custom = new FixedTimeProvider(new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        services.AddSingleton<TimeProvider>(custom);

        BuildValidServicesOnto(services, new TestHostEnvironment(), includeRedis: false);

        services.Count(d => d.ServiceType == typeof(TimeProvider)).Should().Be(1);
        using var sp = services.BuildServiceProvider();
        sp.GetRequiredService<TimeProvider>().Should().BeSameAs(custom);
    }

    private static void BuildValidServicesOnto(
        ServiceCollection services,
        IHostEnvironment environment,
        bool includeRedis = false,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tempKeysPath = Path.Combine(Path.GetTempPath(), "assetblock-dp-tests", Guid.NewGuid().ToString("N"));
        var json = JsonSerializer.Serialize(new
        {
            ConnectionStrings = new
            {
                DefaultConnection = "Host=127.0.0.1;Port=5432;Database=test;Username=u;Password=p",
                Redis = includeRedis ? "localhost:6379" : null
            },
            Jwt = new { Key = new string('k', 32), Issuer = "iss", Audience = "aud", AccessTokenMinutes = 15, RefreshTokenDays = 7 },
            Encryption = new { KeyBase64 = key },
            Storage = new { Provider = "Minio" },
            Minio = new { Endpoint = "http://localhost:9000", Bucket = "assets", AccessKey = "local-access", SecretKey = "local-secret", UseSsl = false },
            SeaweedFs = new { Endpoint = "<seaweedfs-endpoint>:8333", Bucket = "<bucket-name>", AccessKey = "<k>", SecretKey = "<s>", UseSsl = true },
            Stripe = new
            {
                SecretKey = "stripe_test_secret_key_not_real",
                WebhookSecret = "stripe_test_webhook_secret_not_real",
                DefaultSuccessUrl = "http://localhost:3000/payment/success",
                DefaultCancelUrl = "http://localhost:3000/payment/cancel"
            },
            FileUpload = new { MaxFileBytes = 262144000L, AllowedExtensions = new[] { ".zip", ".tar", ".tar.gz", ".tgz" } },
            Email = new
            {
                Provider = "Smtp",
                FromName = "AssetBlock",
                FromAddress = "noreply@localhost",
                PublicAppBaseUrl = "http://localhost:3000",
                MessageIdDomain = "mail.localhost",
                Smtp = new { Host = "localhost", Port = 1025, Security = "NONE", Username = "", Password = "", TimeoutSeconds = 30 }
            },
            DataProtection = new { KeysPath = tempKeysPath },
            AnalyticsRateLimiting = new { BffSigningSecret = new string('s', 32) }
        });
        var configBuilder = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
        if (extra is not null)
        {
            configBuilder.AddInMemoryCollection(extra);
        }

        var config = configBuilder.Build();

        services.AddLogging(b => b.ClearProviders());
        Directory.CreateDirectory(tempKeysPath);
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(tempKeysPath));
        services.AddSingleton(Substitute.For<ITransactionalEmailComposer>());
        services.AddInfrastructure(config, environment);
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private static ServiceCollection BuildValidServices(
        IHostEnvironment environment,
        bool includeRedis = false,
        IReadOnlyDictionary<string, string?>? extra = null)
    {
        var services = new ServiceCollection();
        BuildValidServicesOnto(services, environment, includeRedis, extra);
        return services;
    }

    private static IConfiguration BuildConfig(bool includeRedis)
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var json = JsonSerializer.Serialize(new
        {
            ConnectionStrings = new
            {
                DefaultConnection = "Host=127.0.0.1;Port=5432;Database=test;Username=u;Password=p",
                Redis = includeRedis ? "localhost:6379" : null
            },
            AnalyticsRateLimiting = new { BffSigningSecret = new string('s', 32) },
            Encryption = new { KeyBase64 = key },
            Jwt = new { Key = new string('k', 32), Issuer = "iss", Audience = "aud", AccessTokenMinutes = 15, RefreshTokenDays = 7 },
            Storage = new { Provider = "Minio" },
            Minio = new { Endpoint = "http://localhost:9000", Bucket = "assets", AccessKey = "local-access", SecretKey = "local-secret", UseSsl = false },
            SeaweedFs = new { Endpoint = "<seaweedfs-endpoint>:8333", Bucket = "<bucket-name>", AccessKey = "<k>", SecretKey = "<s>", UseSsl = true },
            Stripe = new
            {
                SecretKey = "stripe_test_secret_key_not_real",
                WebhookSecret = "stripe_test_webhook_secret_not_real",
                DefaultSuccessUrl = "http://localhost:3000/payment/success",
                DefaultCancelUrl = "http://localhost:3000/payment/cancel"
            },
            Email = new
            {
                Provider = "Smtp",
                FromName = "AssetBlock",
                FromAddress = "noreply@localhost",
                PublicAppBaseUrl = "http://localhost:3000",
                MessageIdDomain = "mail.localhost",
                Smtp = new { Host = "localhost", Port = 1025, Security = "NONE", Username = "", Password = "", TimeoutSeconds = 30 }
            },
            DataProtection = new { KeysPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")) }
        });

        return new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
    }
}
