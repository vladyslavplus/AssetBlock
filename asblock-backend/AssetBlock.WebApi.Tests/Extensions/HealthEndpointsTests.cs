using System.Net;
using System.Text.Json;
using AssetBlock.WebApi.Extensions;
using AwesomeAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Hosting;

namespace AssetBlock.WebApi.Tests.Extensions;

public sealed class HealthEndpointsTests
{
    [Fact]
    public async Task HealthLive_WhenHealthy_ShouldReturn200AndConcealDependencyTopology()
    {
        await using WebApplication app = CreateApp(builder =>
        {
            builder.Services.AddHealthChecks()
                .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
                .AddCheck("postgresql", () => HealthCheckResult.Healthy(), tags: ["ready"]);
        });

        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("status", out JsonElement statusElement).Should().BeTrue();
        statusElement.GetString().Should().Be("Healthy");

        root.TryGetProperty("checks", out _).Should().BeFalse();
        root.TryGetProperty("totalDurationMs", out _).Should().BeFalse();
        json.Should().NotContain("self");
        json.Should().NotContain("postgresql");
    }

    [Fact]
    public async Task HealthReady_WhenHealthy_ShouldReturn200AndConcealDependencyTopology()
    {
        await using WebApplication app = CreateApp(builder =>
        {
            builder.Services.AddHealthChecks()
                .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
                .AddCheck("postgresql", () => HealthCheckResult.Healthy(), tags: ["ready"])
                .AddCheck("storage", () => HealthCheckResult.Healthy(), tags: ["ready"]);
        });

        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("status", out JsonElement statusElement).Should().BeTrue();
        statusElement.GetString().Should().Be("Healthy");

        root.TryGetProperty("checks", out _).Should().BeFalse();
        root.TryGetProperty("totalDurationMs", out _).Should().BeFalse();
        json.Should().NotContain("postgresql");
        json.Should().NotContain("storage");
    }

    [Fact]
    public async Task HealthReady_WhenUnhealthy_ShouldReturn503AndNotLeakExceptionOrDependencyDetails()
    {
        await using WebApplication app = CreateApp(builder =>
        {
            builder.Services.AddHealthChecks()
                .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
                .AddCheck("postgresql", () => HealthCheckResult.Unhealthy("Connection to postgresql://secret:pass@db:5432 failed"), tags: ["ready"]);
        });

        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("status", out JsonElement statusElement).Should().BeTrue();
        statusElement.GetString().Should().Be("Unhealthy");

        root.TryGetProperty("checks", out _).Should().BeFalse();
        json.Should().NotContain("postgresql");
        json.Should().NotContain("secret:pass");
        json.Should().NotContain("Connection to");
    }

    [Fact]
    public async Task HealthReady_WhenDegraded_ShouldReturn503Non2xxStatus()
    {
        await using WebApplication app = CreateApp(builder =>
        {
            builder.Services.AddHealthChecks()
                .AddCheck("storage", () => HealthCheckResult.Degraded("High latency detected"), tags: ["ready"]);
        });

        await app.StartAsync();
        HttpClient client = app.GetTestClient();

        HttpResponseMessage response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        var json = await response.Content.ReadAsStringAsync();

        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;

        root.TryGetProperty("status", out JsonElement statusElement).Should().BeTrue();
        statusElement.GetString().Should().Be("Degraded");

        root.TryGetProperty("checks", out _).Should().BeFalse();
        json.Should().NotContain("storage");
        json.Should().NotContain("latency");
    }

    private static WebApplication CreateApp(Action<WebApplicationBuilder> configure)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            EnvironmentName = Environments.Development,
        });
        builder.WebHost.UseTestServer();
        builder.Services.AddRouting();
        configure(builder);

        WebApplication app = builder.Build();
        app.MapAssetBlockHealthChecks();
        return app;
    }
}
