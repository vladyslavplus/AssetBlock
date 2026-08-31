using System.Net;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Infrastructure;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Testcontainers.Redis;

namespace AssetBlock.WebApi.IntegrationTests.ProblemDetails;

public sealed class RedisAnalyticsDistributedRateLimitIntegrationTests : IAsyncLifetime
{
    private RedisContainer? _redis;
    private string _connectionString = "";

    public async Task InitializeAsync()
    {
        _redis = new RedisBuilder("valkey/valkey:9.1.1@sha256:64e361b630ecf18dff7ca4df6a88e6eafc193687eb48cff2c7e0a293ab67d29a").Build();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await _redis.StartAsync(cts.Token);
        _connectionString = _redis.GetConnectionString();
    }

    public async Task DisposeAsync()
    {
        if (_redis is not null)
        {
            await _redis.DisposeAsync();
        }
    }

    [Fact]
    public async Task RedisLimiter_WhenSharedAcrossHosts_ShouldEnforceSinglePartition()
    {
        await using WebApplication appA = await StartHostAsync();
        await using WebApplication appB = await StartHostAsync();
        HttpClient clientA = appA.GetTestClient();
        HttpClient clientB = appB.GetTestClient();
        const string ip = "203.0.113.77";

        await AnalyticsFixedWindowTestGuard.EnsureWindowHasRemainingAsync();
        for (var i = 0; i < RateLimitingConstants.Windows.ANALYTICS_EVENTS_LIMIT; i++)
        {
            HttpResponseMessage ok = await AnalyticsRateLimitTestHost.PostProbeAsync(clientA, ip);
            ok.StatusCode.Should().Be(HttpStatusCode.Accepted, $"request {i + 1} on host A");
        }

        HttpResponseMessage limitedOnB = await AnalyticsRateLimitTestHost.PostProbeAsync(clientB, ip);
        limitedOnB.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        limitedOnB.Headers.RetryAfter.Should().NotBeNull();
    }

    [Fact]
    public async Task RedisLimiter_WhenAtLimit_ShouldReturn429On121stRequest()
    {
        await using WebApplication app = await StartHostAsync();
        HttpClient client = app.GetTestClient();
        const string ip = "203.0.113.78";

        await AnalyticsFixedWindowTestGuard.EnsureWindowHasRemainingAsync();
        for (var i = 0; i < RateLimitingConstants.Windows.ANALYTICS_EVENTS_LIMIT; i++)
        {
            (await AnalyticsRateLimitTestHost.PostProbeAsync(client, ip)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        HttpResponseMessage limited = await AnalyticsRateLimitTestHost.PostProbeAsync(client, ip);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task RedisLimiter_WhenTwoHostsSharePartition_ShouldLimitTogether()
    {
        await using WebApplication appA = await StartHostAsync();
        await using WebApplication appB = await StartHostAsync();
        HttpClient clientA = appA.GetTestClient();
        HttpClient clientB = appB.GetTestClient();
        const string ip = "203.0.113.79";

        await AnalyticsFixedWindowTestGuard.EnsureWindowHasRemainingAsync();
        for (var i = 0; i < RateLimitingConstants.Windows.ANALYTICS_EVENTS_LIMIT - 1; i++)
        {
            (await AnalyticsRateLimitTestHost.PostProbeAsync(clientA, ip)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        (await AnalyticsRateLimitTestHost.PostProbeAsync(clientB, ip)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await AnalyticsRateLimitTestHost.PostProbeAsync(clientA, ip)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
    }

    [Fact]
    public async Task RedisLimiter_WhenUnavailable_ShouldReturn202ForAnalyticsEvents()
    {
        await using WebApplication app = await StartHostAsync("127.0.0.1:1,abortConnect=false,connectTimeout=50");
        HttpClient client = app.GetTestClient();
        HttpResponseMessage response = await AnalyticsRateLimitTestHost.PostProbeAsync(client, "203.0.113.88");
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task RedisLimiter_WhenBffAndDirectPartitionsDiffer_ShouldRateLimitIndependently()
    {
        await using WebApplication app = await StartHostAsync();
        HttpClient client = app.GetTestClient();
        const string remoteIp = "203.0.113.90";
        const string clientIp = "198.51.100.10";

        await AnalyticsFixedWindowTestGuard.EnsureWindowHasRemainingAsync();
        for (var i = 0; i < RateLimitingConstants.Windows.ANALYTICS_EVENTS_LIMIT; i++)
        {
            Dictionary<string, string?> headers = AnalyticsRateLimitTestHost.CreateSignedHeaders(
                clientIp,
                AnalyticsRateLimitTestHost.TEST_SIGNING_SECRET);
            (await AnalyticsRateLimitTestHost.PostProbeAsync(client, remoteIp, headers))
                .StatusCode.Should().Be(HttpStatusCode.Accepted);
        }

        HttpResponseMessage limitedBff = await AnalyticsRateLimitTestHost.PostProbeAsync(
            client,
            remoteIp,
            AnalyticsRateLimitTestHost.CreateSignedHeaders(clientIp, AnalyticsRateLimitTestHost.TEST_SIGNING_SECRET));
        limitedBff.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);

        (await AnalyticsRateLimitTestHost.PostProbeAsync(client, remoteIp))
            .StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    private async Task<WebApplication> StartHostAsync(string? redisConnectionString = null)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        builder.Configuration["AnalyticsRateLimiting:BffSigningSecret"] =
            AnalyticsRateLimitTestHost.TEST_SIGNING_SECRET;
        builder.Configuration["ConnectionStrings:Redis"] = redisConnectionString ?? _connectionString;

        var hostEnvironment = new TestHostEnvironment();
        builder.Services.AddSingleton<IHostEnvironment>(hostEnvironment);
        builder.Services.AddAnalyticsDistributedRateLimiting(builder.Configuration, hostEnvironment);
        builder.Services.AddAnalyticsBffSignatureValidation();
        builder.Services.AddApiRateLimiting();

        WebApplication app = builder.Build();
        app.Use(async (context, next) =>
        {
            var ipHeader = context.Request.Headers["X-Test-Remote-Ip"].ToString();
            if (!string.IsNullOrWhiteSpace(ipHeader))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ipHeader);
            }

            await next();
        });
        app.UseAnalyticsBffSignatureValidation();
        app.UseRateLimiter();
        app.MapPost("/api/analytics/events", () => Microsoft.AspNetCore.Http.Results.Accepted())
            .RequireRateLimiting(RateLimitingConstants.Policies.ANALYTICS_EVENTS);
        await app.StartAsync();
        return app;
    }
}
