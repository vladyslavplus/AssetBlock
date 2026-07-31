using System.Net;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Infrastructure;
using AssetBlock.WebApi.Extensions;
using AssetBlock.WebApi.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Support;

internal static class AnalyticsRateLimitTestHost
{
    private const string TEST_REMOTE_IP_HEADER = "X-Test-Remote-Ip";
    internal const string TEST_SIGNING_SECRET = AssetBlockWebApplicationFactory.TEST_ANALYTICS_BFF_SIGNING_SECRET;
    private const string PROBE_PATH = "/api/analytics/events";

    internal static async Task<WebApplication> StartAsync(string? signingSecret = TEST_SIGNING_SECRET)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();

        if (signingSecret is not null)
        {
            builder.Configuration["AnalyticsRateLimiting:BffSigningSecret"] = signingSecret;
        }

        builder.Services.AddAnalyticsRateLimitingOptions(builder.Configuration);
        builder.Services.AddAnalyticsBffSignatureValidation();
        builder.Services.AddApiRateLimiting();

        var app = builder.Build();

        app.Use(async (context, next) =>
        {
            var ipHeader = context.Request.Headers[TEST_REMOTE_IP_HEADER].ToString();
            if (!string.IsNullOrWhiteSpace(ipHeader))
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(ipHeader);
            }

            await next();
        });
        app.UseAnalyticsBffSignatureValidation();
        app.UseRateLimiter();
        app.MapPost(PROBE_PATH, () => Microsoft.AspNetCore.Http.Results.Accepted())
            .RequireRateLimiting(RateLimitingConstants.Policies.ANALYTICS_EVENTS);

        await app.StartAsync();
        return app;
    }

    internal static Dictionary<string, string?> CreateSignedHeaders(
        string normalizedClientIp,
        string secret,
        long? timestampUnixSeconds = null)
    {
        var partition = AnalyticsBffSignatureHelper.CreatePartition(normalizedClientIp, secret);
        var timestamp = (timestampUnixSeconds ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds()).ToString();
        var signature = AnalyticsBffSignatureHelper.CreateRequestSignature(timestamp, partition, secret);

        return new Dictionary<string, string?>
        {
            [AnalyticsBffRateLimitHeaders.PARTITION] = partition,
            [AnalyticsBffRateLimitHeaders.TIMESTAMP] = timestamp,
            [AnalyticsBffRateLimitHeaders.SIGNATURE] = signature,
        };
    }

    internal static async Task<HttpResponseMessage> PostProbeAsync(
        HttpClient client,
        string? remoteIp = null,
        IReadOnlyDictionary<string, string?>? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(PROBE_PATH, UriKind.Relative));
        if (!string.IsNullOrWhiteSpace(remoteIp))
        {
            request.Headers.TryAddWithoutValidation(TEST_REMOTE_IP_HEADER, remoteIp);
        }

        if (headers is not null)
        {
            foreach (var (name, value) in headers)
            {
                if (value is not null)
                {
                    request.Headers.TryAddWithoutValidation(name, value);
                }
            }
        }

        return await client.SendAsync(request);
    }
}
