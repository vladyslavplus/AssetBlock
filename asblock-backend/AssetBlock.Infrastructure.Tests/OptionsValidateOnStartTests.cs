using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure;
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
            encryptionKeyBase64: "not-valid-base64!!");

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
        string? encryptionKeyBase64 = null,
        string? analyticsSigningSecret = null)
    {
        var key = encryptionKeyBase64 ?? Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tempKeysPath = Path.Combine(Path.GetTempPath(), "assetblock-dp-tests", Guid.NewGuid().ToString("N"));
        var json = JsonSerializer.Serialize(new
        {
            ConnectionStrings = new { DefaultConnection = "Host=127.0.0.1;Port=5432;Database=test;Username=u;Password=p" },
            Jwt = new { Key = new string('k', 32), Issuer = "iss", Audience = "aud", AccessTokenMinutes = 15, RefreshTokenDays = 7 },
            Encryption = new { KeyBase64 = key },
            Minio = new { Endpoint = "http://localhost:9000", Bucket = "assets", AccessKey = "local-access", SecretKey = "local-secret", UseSsl = false },
            Stripe = new
            {
                SecretKey = "stripe_test_secret_key_not_real",
                WebhookSecret = "stripe_test_webhook_secret_not_real",
                DefaultSuccessUrl = "http://localhost:3000/payment/success",
                DefaultCancelUrl = "http://localhost:3000/payment/cancel"
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
