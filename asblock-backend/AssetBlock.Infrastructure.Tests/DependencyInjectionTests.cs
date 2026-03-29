using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Infrastructure.Tests;

public sealed class DependencyInjectionTests
{
    [Fact]
    public void AddInfrastructure_resolves_core_services()
    {
        var key = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        var json = JsonSerializer.Serialize(new
        {
            ConnectionStrings = new { DefaultConnection = "Host=127.0.0.1;Port=5432;Database=test;Username=u;Password=p" },
            Jwt = new { Key = new string('k', 32), Issuer = "iss", Audience = "aud" },
            Encryption = new { KeyBase64 = key },
            Minio = new { Endpoint = "localhost:9000", Bucket = "assets", AccessKey = "k", SecretKey = "s", UseSsl = false },
            Stripe = new { SecretKey = "sk_test_123456789012345678901234567890" },
            Elasticsearch = new { Url = "http://localhost:9200", DefaultIndex = "assets" }
        });
        var config = new ConfigurationBuilder()
            .AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)))
            .Build();

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders());
        services.AddInfrastructure(config);

        using var sp = services.BuildServiceProvider();

        sp.GetRequiredService<IJwtTokenService>().Should().NotBeNull();
        sp.GetRequiredService<IUserStore>().Should().NotBeNull();
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
        sp.GetRequiredService<IAssetSearchService>().Should().NotBeNull();
        sp.GetRequiredService<ICacheService>().Should().NotBeNull();
        sp.GetRequiredService<ApplicationDbContext>();
    }
}
