using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddInfrastructure_resolves_core_services()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
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
            FileUpload = new { MaxFileBytes = 262144000L, AllowedExtensions = new[] { ".zip", ".7z", ".rar", ".tar", ".tar.gz", ".tgz" } },
            Email = new
            {
                Provider = "Smtp",
                FromName = "AssetBlock",
                FromAddress = "noreply@localhost",
                PublicAppBaseUrl = "http://localhost:3000",
                MessageIdDomain = "mail.localhost",
                Smtp = new { Host = "localhost", Port = 1025, Security = "NONE", Username = "", Password = "", TimeoutSeconds = 30 }
            },
            DataProtection = new { KeysPath = tempKeysPath }
        });
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        Directory.CreateDirectory(tempKeysPath);
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(tempKeysPath));
        services.AddSingleton(Substitute.For<ITransactionalEmailComposer>());
        services.AddInfrastructure(config);

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
            Minio = new { Endpoint = "http://localhost:9000", Bucket = "assets", AccessKey = "local-access", SecretKey = "local-secret", UseSsl = false },
            Stripe = new
            {
                SecretKey = "stripe_test_secret_key_not_real",
                WebhookSecret = "stripe_test_webhook_secret_not_real",
                DefaultSuccessUrl = "http://localhost:3000/payment/success",
                DefaultCancelUrl = "http://localhost:3000/payment/cancel"
            },
            FileUpload = new { MaxFileBytes = 262144000L, AllowedExtensions = new[] { ".zip", ".7z", ".rar", ".tar", ".tar.gz", ".tgz" } },
            Email = new
            {
                Provider = "Smtp",
                FromName = "AssetBlock",
                FromAddress = "noreply@localhost",
                PublicAppBaseUrl = "http://localhost:3000",
                MessageIdDomain = "mail.localhost",
                Smtp = new { Host = "localhost", Port = 1025, Security = "NONE", Username = "", Password = "", TimeoutSeconds = 30 }
            },
            DataProtection = new { KeysPath = tempKeysPath }
        });
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        Directory.CreateDirectory(tempKeysPath);
        services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(tempKeysPath));
        services.AddSingleton(Substitute.For<ITransactionalEmailComposer>());
        services.AddInfrastructure(config);

        using var sp = services.BuildServiceProvider();
        var act = () => _ = sp.GetRequiredService<IOptions<EncryptionOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }
}
