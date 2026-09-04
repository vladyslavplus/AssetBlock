using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Minio;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests;

public sealed class AssetStorageDiTests
{
    [Theory]
    [InlineData("SeaweedFs", typeof(S3CompatibleAssetStorageService))]
    [InlineData("seaweedfs", typeof(S3CompatibleAssetStorageService))]
    [InlineData("Minio", typeof(S3CompatibleAssetStorageService))]
    [InlineData("MINIO", typeof(S3CompatibleAssetStorageService))]
    public void AddInfrastructure_WhenProviderSelected_ShouldRegisterExactlyOneStorageAdapter(string provider, Type expectedType)
    {
        ServiceCollection services = BuildServices(provider);
        using ServiceProvider sp = services.BuildServiceProvider();

        services.Count(d => d.ServiceType == typeof(IAssetStorageService)).Should().Be(1);
        services.Count(d => d.ServiceType == typeof(IMinioClient)).Should().Be(1);

        IAssetStorageService storage = sp.GetRequiredService<IAssetStorageService>();
        storage.Should().BeOfType(expectedType);
        sp.GetRequiredService<IMinioClient>().Should().NotBeNull();
    }

    [Fact]
    public void AddInfrastructure_WhenProviderUnknown_ShouldFailFast()
    {
        Func<ServiceCollection> act = () => BuildServices("AzureBlob");
        act.Should().Throw<InvalidOperationException>().WithMessage("*unknown*");
    }

    [Fact]
    public void AddInfrastructure_WhenProviderMissing_ShouldFailFast()
    {
        Func<ServiceCollection> act = () => BuildServices(provider: null);
        act.Should().Throw<InvalidOperationException>().WithMessage("*required*");
    }

    [Fact]
    public void AddInfrastructure_WhenSeaweedFsSelected_ShouldIgnoreInvalidMinioPlaceholders()
    {
        ServiceCollection services = BuildServices(
            "SeaweedFs",
            seaweedEndpoint: "http://127.0.0.1:8333",
            seaweedAccess: "ak",
            seaweedSecret: "sk",
            minioEndpoint: "<minio-endpoint>:9000",
            minioAccess: "<minio-access-key>",
            minioSecret: "<minio-secret-key>");

        using ServiceProvider sp = services.BuildServiceProvider();
        Action act = () =>
        {
            _ = sp.GetRequiredService<IOptions<StorageOptions>>().Value;
            _ = sp.GetRequiredService<IOptions<SeaweedFsOptions>>().Value;
            _ = sp.GetRequiredService<IOptions<MinioOptions>>().Value;
        };

        act.Should().NotThrow();
        sp.GetRequiredService<IAssetStorageService>().Should().BeOfType<S3CompatibleAssetStorageService>();
    }

    [Fact]
    public void AddInfrastructure_WhenMinioSelected_ShouldValidateMinioAndIgnoreSeaweedPlaceholders()
    {
        ServiceCollection services = BuildServices(
            "Minio",
            seaweedEndpoint: "<seaweedfs-endpoint>:8333",
            seaweedAccess: "<seaweedfs-access-key>",
            seaweedSecret: "<seaweedfs-secret-key>",
            minioEndpoint: "http://127.0.0.1:9000",
            minioAccess: "ak",
            minioSecret: "sk");

        using ServiceProvider sp = services.BuildServiceProvider();
        Action act = () =>
        {
            _ = sp.GetRequiredService<IOptions<MinioOptions>>().Value;
            _ = sp.GetRequiredService<IOptions<SeaweedFsOptions>>().Value;
        };

        act.Should().NotThrow();
        sp.GetRequiredService<IAssetStorageService>().Should().BeOfType<S3CompatibleAssetStorageService>();
    }

    private static ServiceCollection BuildServices(
        string? provider,
        string seaweedEndpoint = "http://127.0.0.1:8333",
        string seaweedAccess = "seaweed-access",
        string seaweedSecret = "seaweed-secret",
        string minioEndpoint = "http://127.0.0.1:9000",
        string minioAccess = "minio-access",
        string minioSecret = "minio-secret")
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var tempKeysPath = Path.Combine(Path.GetTempPath(), "assetblock-dp-tests", Guid.NewGuid().ToString("N"));
        var json = JsonSerializer.Serialize(new
        {
            ConnectionStrings = new { DefaultConnection = "Host=127.0.0.1;Port=5432;Database=test;Username=u;Password=p" },
            Jwt = new { Key = new string('k', 32), Issuer = "iss", Audience = "aud", AccessTokenMinutes = 15, RefreshTokenDays = 7, HubAudience = "hub", HubTokenSeconds = 90 },
            Encryption = new { CurrentKeyId = "k1", Keys = new Dictionary<string, string> { ["k1"] = key } },
            Storage = new { Provider = provider },
            SeaweedFs = new
            {
                Endpoint = seaweedEndpoint,
                Bucket = "assets",
                AccessKey = seaweedAccess,
                SecretKey = seaweedSecret,
                UseSsl = false
            },
            Minio = new
            {
                Endpoint = minioEndpoint,
                Bucket = "assets",
                AccessKey = minioAccess,
                SecretKey = minioSecret,
                UseSsl = false
            },
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
            AnalyticsRateLimiting = new { BffSigningSecret = new string('s', 32) }
        });

        IConfigurationRoot config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        Directory.CreateDirectory(tempKeysPath);
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(tempKeysPath));
        services.AddSingleton(Substitute.For<ITransactionalEmailComposer>());
        services.AddInfrastructure(config, new TestHostEnvironment());
        return services;
    }
}
