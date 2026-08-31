using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests;

public sealed class OptionsValidateOnStartTests
{
    [Fact]
    public void BuildServiceProvider_WhenEncryptionKeyInvalid_ShouldThrowOptionsValidationException()
    {
        var services = BuildInfrastructureServices(
            new TestHostEnvironment { EnvironmentName = Environments.Development },
            encryptionKey: "not-valid-base64!!");

        var act = () =>
        {
            using var sp = services.BuildServiceProvider();
            _ = sp.GetRequiredService<IOptions<EncryptionOptions>>().Value;
        };

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void BuildServiceProvider_WhenCurrentKeyIdMissing_ShouldThrowOptionsValidationException()
    {
        var services = BuildInfrastructureServices(
            new TestHostEnvironment { EnvironmentName = Environments.Development },
            encryptionCurrentKeyId: null);

        var act = () =>
        {
            using var sp = services.BuildServiceProvider();
            _ = sp.GetRequiredService<IOptions<EncryptionOptions>>().Value;
        };

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public void BuildServiceProvider_WhenCurrentKeyIdExplicitlyEmpty_ShouldThrowOptionsValidationException()
    {
        var services = BuildInfrastructureServices(
            new TestHostEnvironment { EnvironmentName = Environments.Development },
            encryptionCurrentKeyId: "");

        var act = () =>
        {
            using var sp = services.BuildServiceProvider();
            _ = sp.GetRequiredService<IOptions<EncryptionOptions>>().Value;
        };

        act.Should().Throw<OptionsValidationException>();
    }

    [Fact]
    public async Task HostStart_WhenAnalyticsSigningSecretTooShort_ShouldThrowOptionsValidationException()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Environment.EnvironmentName = Environments.Development;
        builder.Configuration["AnalyticsRateLimiting:BffSigningSecret"] = "too-short";
        builder.Services.AddAnalyticsRateLimitingOptions(builder.Configuration);

        using var host = builder.Build();
        var act = async () => await host.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>();
    }

    [Fact]
    public void AddAnalyticsDistributedRateLimiting_WhenIntegrationTestingWithoutRedis_ShouldNotThrow()
    {
        var services = new ServiceCollection();
        var config = BuildMinimalConfig(includeRedis: false);
        var env = new TestHostEnvironment { EnvironmentName = "IntegrationTesting" };

        var act = () => services.AddAnalyticsDistributedRateLimiting(config, env);

        act.Should().NotThrow();
    }

    [Fact]
    public void AddAnalyticsDistributedRateLimiting_WhenStagingWithoutRedis_ShouldThrow()
    {
        var services = new ServiceCollection();
        var config = BuildMinimalConfig(includeRedis: false);
        var env = new TestHostEnvironment { EnvironmentName = Environments.Staging };

        var act = () => services.AddAnalyticsDistributedRateLimiting(config, env);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Redis*");
    }

    private static ServiceCollection BuildInfrastructureServices(
        IHostEnvironment environment,
        string? encryptionKey = null,
        string? encryptionCurrentKeyId = "k1",
        string? analyticsSigningSecret = null)
    {
        var key = encryptionKey ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var encryptionSection = encryptionCurrentKeyId is not null
            ? new { CurrentKeyId = encryptionCurrentKeyId, Keys = new Dictionary<string, string> { ["k1"] = key } }
            : (object)new { Keys = new Dictionary<string, string> { ["k1"] = key } };
        var tempKeysPath = Path.Combine(Path.GetTempPath(), "assetblock-dp-tests", Guid.NewGuid().ToString("N"));
        var json = JsonSerializer.Serialize(new
        {
            ConnectionStrings = new { DefaultConnection = "Host=127.0.0.1;Port=5432;Database=test;Username=u;Password=p" },
            Jwt = new { Key = new string('k', 32), Issuer = "iss", Audience = "aud", AccessTokenMinutes = 15, RefreshTokenDays = 7, HubAudience = "hub", HubTokenSeconds = 90 },
            Encryption = encryptionSection,
            Storage = new { Provider = "Minio" },
            Minio = new { Endpoint = "http://localhost:9000", Bucket = "assets", AccessKey = "local-access", SecretKey = "local-secret", UseSsl = false },
            SeaweedFs = new { Endpoint = "<seaweedfs-endpoint>:8333", Bucket = "<bucket-name>", AccessKey = "<k>", SecretKey = "<s>", UseSsl = true },
            Stripe = new
            {
                SecretKey = "stripe_test_secret_key_not_real",
                WebhookSecret = "stripe_test_webhook_secret_not_real",
                SuccessUrl = "http://localhost:3000/checkout/success",
                CancelUrl = "http://localhost:3000/checkout/cancel"
            },
            FileUpload = new { MaxFileBytes = 262144000L, AllowedExtensions = new[] { ".zip" } },
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
            AnalyticsRateLimiting = new { BffSigningSecret = analyticsSigningSecret ?? new string('s', 32) }
        });

        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        Directory.CreateDirectory(tempKeysPath);
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(tempKeysPath));
        services.AddSingleton(Substitute.For<ITransactionalEmailComposer>());
        services.AddInfrastructure(config, environment);
        return services;
    }

    private static IConfiguration BuildMinimalConfig(bool includeRedis)
    {
        var json = JsonSerializer.Serialize(new
        {
            ConnectionStrings = new
            {
                DefaultConnection = "Host=127.0.0.1;Port=5432;Database=test;Username=u;Password=p",
                Redis = includeRedis ? "localhost:6379" : null
            },
            AnalyticsRateLimiting = new { BffSigningSecret = new string('s', 32) }
        });

        return new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();
    }
}
