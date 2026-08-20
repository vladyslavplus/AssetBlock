using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure;
using AssetBlock.WebApi.Services;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace AssetBlock.WebApi.Tests.Services;

public sealed class AnalyticsBffSignatureValidatorTests
{
    private const string TEST_SECRET = "unit_test_analytics_bff_signing_secret_32";

    [Fact]
    public void Validate_WhenNoHeaders_ShouldReturnNoHeaders()
    {
        var validator = CreateValidator(TEST_SECRET);
        var context = new DefaultHttpContext();

        var result = validator.Validate(context);

        result.Outcome.Should().Be(AnalyticsBffSignatureValidationOutcome.NO_HEADERS);
    }

    [Fact]
    public void Validate_WhenValidHeaders_ShouldReturnVerifiedPartition()
    {
        var validator = CreateValidator(TEST_SECRET);
        var context = new DefaultHttpContext();
        const string clientIp = "198.51.100.42";
        var partition = AnalyticsBffSignatureHelper.CreatePartition(clientIp, TEST_SECRET);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = AnalyticsBffSignatureHelper.CreateRequestSignature(timestamp, partition, TEST_SECRET);

        context.Request.Headers[AnalyticsBffRateLimitHeaders.PARTITION] = partition;
        context.Request.Headers[AnalyticsBffRateLimitHeaders.TIMESTAMP] = timestamp;
        context.Request.Headers[AnalyticsBffRateLimitHeaders.SIGNATURE] = signature;

        var result = validator.Validate(context);

        result.Outcome.Should().Be(AnalyticsBffSignatureValidationOutcome.VALID);
        result.VerifiedPartition.Should().Be(partition);
    }

    [Fact]
    public void Validate_WhenSignatureDiffersByOneCharacter_ShouldReturnInvalid()
    {
        var validator = CreateValidator(TEST_SECRET);
        var context = new DefaultHttpContext();
        const string clientIp = "198.51.100.43";
        var partition = AnalyticsBffSignatureHelper.CreatePartition(clientIp, TEST_SECRET);
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        var signature = AnalyticsBffSignatureHelper.CreateRequestSignature(timestamp, partition, TEST_SECRET);
        var tampered = signature[..^1] + (signature[^1] == 'a' ? 'b' : 'a');

        context.Request.Headers[AnalyticsBffRateLimitHeaders.PARTITION] = partition;
        context.Request.Headers[AnalyticsBffRateLimitHeaders.TIMESTAMP] = timestamp;
        context.Request.Headers[AnalyticsBffRateLimitHeaders.SIGNATURE] = tampered;

        var result = validator.Validate(context);

        result.Outcome.Should().Be(AnalyticsBffSignatureValidationOutcome.INVALID);
    }

    [Fact]
    public void Validate_WhenTimestampOutsideTolerance_ShouldReturnInvalid()
    {
        var validator = CreateValidator(TEST_SECRET);
        var context = new DefaultHttpContext();
        const string clientIp = "198.51.100.44";
        var partition = AnalyticsBffSignatureHelper.CreatePartition(clientIp, TEST_SECRET);
        var staleTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds().ToString();
        var signature = AnalyticsBffSignatureHelper.CreateRequestSignature(staleTimestamp, partition, TEST_SECRET);

        context.Request.Headers[AnalyticsBffRateLimitHeaders.PARTITION] = partition;
        context.Request.Headers[AnalyticsBffRateLimitHeaders.TIMESTAMP] = staleTimestamp;
        context.Request.Headers[AnalyticsBffRateLimitHeaders.SIGNATURE] = signature;

        var result = validator.Validate(context);

        result.Outcome.Should().Be(AnalyticsBffSignatureValidationOutcome.INVALID);
    }

    [Fact]
    public void Validate_WhenBackendSecretMissingAndHeadersPresent_ShouldReturnInvalid()
    {
        var validator = CreateValidator(string.Empty);
        var context = new DefaultHttpContext();
        context.Request.Headers[AnalyticsBffRateLimitHeaders.PARTITION] = new string('e', 64);
        context.Request.Headers[AnalyticsBffRateLimitHeaders.TIMESTAMP] =
            DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        context.Request.Headers[AnalyticsBffRateLimitHeaders.SIGNATURE] = new string('f', 64);

        var result = validator.Validate(context);

        result.Outcome.Should().Be(AnalyticsBffSignatureValidationOutcome.INVALID);
    }

    [Fact]
    public void CreatePartitionAndSignature_WhenGoldenVector_ShouldMatchHardCodedCrossRuntimeValues()
    {
        // Keep in sync with asblock-frontend/lib/server/analytics-bff-signature.ts golden vector comment.
        const string secret = "golden_vector_analytics_bff_signing_secret_v1";
        const string clientIp = "203.0.113.10";
        const string timestamp = "1700000000";
        const string expectedPartition = "a862646e90e49ba9447d3f44225e5cb2acf8252f4f74bf9333b66cd3ab56b22a";
        const string expectedSignature = "9131820c941bc95d780d35ebbe4b0de9374d331d361edd693b03a849d0155467";

        var partition = AnalyticsBffSignatureHelper.CreatePartition(clientIp, secret);
        var signature = AnalyticsBffSignatureHelper.CreateRequestSignature(timestamp, partition, secret);

        partition.Should().Be(expectedPartition);
        signature.Should().Be(expectedSignature);
    }

    [Fact]
    public async Task AddAnalyticsRateLimitingOptions_WhenProductionSecretMissing_ShouldFailOnStart()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAnalyticsRateLimitingOptions(builder.Configuration);

        await using var app = builder.Build();
        var act = async () => await app.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>()
            .Where(ex => ex.Failures.Any(f => f.Contains("AnalyticsRateLimiting:BffSigningSecret")));
    }

    [Fact]
    public async Task AddAnalyticsRateLimitingOptions_WhenStagingSecretMissing_ShouldFailOnStart()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "Staging",
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAnalyticsRateLimitingOptions(builder.Configuration);

        await using var app = builder.Build();
        var act = async () => await app.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>()
            .Where(ex => ex.Failures.Any(f => f.Contains("AnalyticsRateLimiting:BffSigningSecret")));
    }

    [Fact]
    public async Task AddAnalyticsRateLimitingOptions_WhenProductionSecretTooShort_ShouldFailOnStart()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Production,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration["AnalyticsRateLimiting:BffSigningSecret"] = "too-short";
        builder.Services.AddAnalyticsRateLimitingOptions(builder.Configuration);

        await using var app = builder.Build();
        var act = async () => await app.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>()
            .Where(ex => ex.Failures.Any(f => f.Contains("at least 32 characters")));
    }

    [Fact]
    public async Task AddAnalyticsRateLimitingOptions_WhenDevelopmentSecretMissing_ShouldStart()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAnalyticsRateLimitingOptions(builder.Configuration);

        await using var app = builder.Build();
        var act = async () => await app.StartAsync();

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task AddAnalyticsRateLimitingOptions_WhenDevelopmentSecretTooShort_ShouldFailOnStart()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Configuration["AnalyticsRateLimiting:BffSigningSecret"] = "too-short";
        builder.Services.AddAnalyticsRateLimitingOptions(builder.Configuration);

        await using var app = builder.Build();
        var act = async () => await app.StartAsync();

        await act.Should().ThrowAsync<OptionsValidationException>()
            .Where(ex => ex.Failures.Any(f => f.Contains("at least 32 characters")));
    }

    [Fact]
    public async Task AddAnalyticsRateLimitingOptions_WhenIntegrationTestingSecretMissing_ShouldStart()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = "IntegrationTesting",
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddAnalyticsRateLimitingOptions(builder.Configuration);

        await using var app = builder.Build();
        var act = async () => await app.StartAsync();

        await act.Should().NotThrowAsync();
    }

    private static AnalyticsBffSignatureValidator CreateValidator(string secret)
    {
        var options = Options.Create(new AnalyticsRateLimitingOptions { BffSigningSecret = secret });
        return new AnalyticsBffSignatureValidator(options);
    }
}
