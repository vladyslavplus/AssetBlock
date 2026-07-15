using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.Tests;

public sealed class OptionsValidateOnStartTests
{
    [Fact]
    public async Task HostStart_WhenRequiredOptionsInvalid_ShouldThrowOptionsValidationException()
    {
        var json = JsonSerializer.Serialize(new
        {
            ConnectionStrings = new { DefaultConnection = "Host=127.0.0.1;Port=5432;Database=test;Username=u;Password=p" },
            Jwt = new
            {
                Issuer = "<AssetBlock>",
                Audience = "<AssetBlock.Api>",
                Key = "<dev-secret-key-min-32-characters-long-for-hmac>",
                AccessTokenMinutes = 15,
                RefreshTokenDays = 7
            },
            Encryption = new { KeyBase64 = "<base64-encoded-encryption-key>" },
            Minio = new
            {
                Endpoint = "<minio-endpoint>:9000",
                Bucket = "<bucket-name>",
                AccessKey = "<minio-access-key>",
                SecretKey = "<minio-secret-key>",
                UseSsl = true
            },
            Stripe = new
            {
                SecretKey = "<stripe-secret-key>",
                WebhookSecret = "<stripe-webhook-secret>",
                DefaultSuccessUrl = "<default-success-url>",
                DefaultCancelUrl = "<default-cancel-url>"
            }
        });

        using var host = Host.CreateDefaultBuilder()
            .ConfigureAppConfiguration((_, config) =>
            {
                config.Sources.Clear();
                config.AddJsonStream(new MemoryStream(Encoding.UTF8.GetBytes(json)));
            })
            .ConfigureServices((ctx, services) =>
            {
                services.AddLogging(b => b.ClearProviders());
                services.AddInfrastructure(ctx.Configuration);
            })
            .Build();

        Exception? caught = null;
        try
        {
            await host.StartAsync();
        }
        catch (Exception ex)
        {
            caught = ex;
        }

        caught.Should().NotBeNull("host start must fail on invalid options");
        Flatten(caught)
            .OfType<OptionsValidationException>()
            .Should()
            .NotBeEmpty("ValidateOnStart must surface OptionsValidationException before the host is usable");
    }

    private static IEnumerable<Exception> Flatten(Exception exception)
    {
        if (exception is AggregateException aggregate)
        {
            return aggregate.Flatten().InnerExceptions;
        }

        return [exception];
    }
}
