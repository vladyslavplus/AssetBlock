using System.Net;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace AssetBlock.WebApi.Tests.Extensions;

public sealed class CorsExtensionsTests
{
    private const string ALLOWED_ORIGIN = "http://localhost:3000";

    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    [InlineData("PUT")]
    [InlineData("PATCH")]
    [InlineData("DELETE")]
    [InlineData("HEAD")]
    [InlineData("OPTIONS")]
    public async Task Preflight_WhenAllowedMethod_ShouldAllow(string method)
    {
        await using var app = CreateApp([ALLOWED_ORIGIN]);
        await app.StartAsync();
        var client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/api/test");
        request.Headers.Add("Origin", ALLOWED_ORIGIN);
        request.Headers.Add("Access-Control-Request-Method", method);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
        response.Headers.GetValues("Access-Control-Allow-Origin").Should().Contain(ALLOWED_ORIGIN);
        response.Headers.GetValues("Access-Control-Allow-Methods").Should().Contain(m => m.Contains(method));
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("Content-Type")]
    [InlineData("Accept")]
    [InlineData("Origin")]
    [InlineData("X-Requested-With")]
    [InlineData("X-SignalR-User-Agent")]
    [InlineData("x-ms-signalr-request-id")]
    [InlineData(AnalyticsBffRateLimitHeaders.PARTITION)]
    [InlineData(AnalyticsBffRateLimitHeaders.TIMESTAMP)]
    [InlineData(AnalyticsBffRateLimitHeaders.SIGNATURE)]
    public async Task Preflight_WhenAllowedHeader_ShouldAllow(string header)
    {
        await using var app = CreateApp([ALLOWED_ORIGIN]);
        await app.StartAsync();
        var client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/api/test");
        request.Headers.Add("Origin", ALLOWED_ORIGIN);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", header);

        var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
        response.Headers.Contains("Access-Control-Allow-Origin").Should().BeTrue();
        response.Headers.GetValues("Access-Control-Allow-Headers").Should().Contain(h => h.Contains(header, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Preflight_WhenUnknownMethod_ShouldReject()
    {
        await using var app = CreateApp([ALLOWED_ORIGIN]);
        await app.StartAsync();
        var client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/api/test");
        request.Headers.Add("Origin", ALLOWED_ORIGIN);
        request.Headers.Add("Access-Control-Request-Method", "TRACE");

        var response = await client.SendAsync(request);

        // Kestrel/ASP.NET Core CORS middleware does not return Access-Control-Allow-Methods for disallowed methods on preflight
        if (response.Headers.TryGetValues("Access-Control-Allow-Methods", out var allowedMethods))
        {
            allowedMethods.Should().NotContain(m => m.Contains("TRACE"));
        }
        else
        {
            response.Headers.Contains("Access-Control-Allow-Methods").Should().BeFalse();
        }
    }

    [Fact]
    public async Task Preflight_WhenUnknownHeader_ShouldReject()
    {
        await using var app = CreateApp([ALLOWED_ORIGIN]);
        await app.StartAsync();
        var client = app.GetTestClient();

        var request = new HttpRequestMessage(HttpMethod.Options, "/api/test");
        request.Headers.Add("Origin", ALLOWED_ORIGIN);
        request.Headers.Add("Access-Control-Request-Method", "POST");
        request.Headers.Add("Access-Control-Request-Headers", "X-Unknown-Malicious-Header");

        var response = await client.SendAsync(request);

        // Kestrel/ASP.NET Core CORS middleware does not permit the unknown header in Access-Control-Allow-Headers
        if (response.Headers.TryGetValues("Access-Control-Allow-Headers", out var allowedHeaders))
        {
            allowedHeaders.Should().NotContain(h => h.Contains("X-Unknown-Malicious-Header", StringComparison.OrdinalIgnoreCase));
        }
    }

    private static WebApplication CreateApp(string[] allowedOrigins)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development
        });
        builder.WebHost.UseTestServer();

        var configData = new Dictionary<string, string?>();
        for (var i = 0; i < allowedOrigins.Length; i++)
        {
            configData[$"Cors:AllowedOrigins:{i}"] = allowedOrigins[i];
        }

        builder.Configuration.AddInMemoryCollection(configData);
        builder.Services.AddAssetBlockCors(builder.Configuration, builder.Environment);

        var app = builder.Build();
        app.UseAssetBlockCors();
        app.MapPost("/api/test", () => Microsoft.AspNetCore.Http.Results.Ok());

        return app;
    }
}
