using System.Net;
using System.Text.Json;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;

namespace AssetBlock.WebApi.IntegrationTests.HealthChecks;

[Collection(nameof(IntegrationTestCollection))]
public sealed class HealthEndpointsIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Live_WhenProcessIsRunning_ShouldReturnHealthyJson()
    {
        HttpClient client = fixture.Factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(stream);
        document.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        document.RootElement.TryGetProperty("checks", out _).Should().BeFalse();
        document.RootElement.TryGetProperty("totalDurationMs", out _).Should().BeFalse();
    }

    [Fact]
    public async Task Ready_WhenDependenciesAreHealthy_ShouldReturnHealthyJson()
    {
        HttpClient client = fixture.Factory.CreateClient();

        HttpResponseMessage response = await client.GetAsync(new Uri("/health/ready", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        await using Stream stream = await response.Content.ReadAsStreamAsync();
        using JsonDocument document = await JsonDocument.ParseAsync(stream);
        document.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        document.RootElement.TryGetProperty("checks", out _).Should().BeFalse();
        document.RootElement.TryGetProperty("totalDurationMs", out _).Should().BeFalse();
    }
}
