using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace AssetBlock.WebApi.IntegrationTests.Support;

/// <summary>WebApplicationFactory with Postgres connection string and integration-only settings.</summary>
public sealed class AssetBlockWebApplicationFactory(string connectionString) : WebApplicationFactory<Program>
{
    private const string TEST_JWT_KEY = "integration_test_jwt_signing_key_min_32_chars!";
    // Synthetic 32 zero bytes (Base64) — not a production key.
    private const string TEST_ENCRYPTION_KEY_BASE64 = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=";

    private readonly string _connectionString = connectionString ?? throw new ArgumentNullException(nameof(connectionString));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("IntegrationTesting");

        // UseSetting wins over appsettings.json placeholders when the test host merges configuration.
        builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
        builder.UseSetting("ConnectionStrings:Redis", string.Empty);
        builder.UseSetting("Database:AutoMigrate", "true");
        builder.UseSetting("Database:EnsureCreated", "false");
        builder.UseSetting("Jwt:Key", TEST_JWT_KEY);
        builder.UseSetting("Jwt:Issuer", "AssetBlock.Integration");
        builder.UseSetting("Jwt:Audience", "AssetBlock.Integration.Api");
        builder.UseSetting("Jwt:AccessTokenMinutes", "60");
        builder.UseSetting("Jwt:RefreshTokenDays", "7");
        builder.UseSetting("Minio:Endpoint", "http://127.0.0.1:9000");
        builder.UseSetting("Minio:Bucket", "assets");
        builder.UseSetting("Minio:AccessKey", "minioadmin");
        builder.UseSetting("Minio:SecretKey", "minioadmin");
        builder.UseSetting("Minio:UseSsl", "false");
        builder.UseSetting("Encryption:KeyBase64", TEST_ENCRYPTION_KEY_BASE64);
        builder.UseSetting("Stripe:SecretKey", "stripe_integration_secret_key_not_real");
        builder.UseSetting("Stripe:WebhookSecret", "stripe_integration_webhook_secret_not_real");
        builder.UseSetting("Stripe:DefaultSuccessUrl", "http://localhost/success");
        builder.UseSetting("Stripe:DefaultCancelUrl", "http://localhost/cancel");
        builder.UseSetting("Elasticsearch:Url", "http://127.0.0.1:9200");
        builder.UseSetting("Elasticsearch:DefaultIndex", "assets-integration");
    }
}
