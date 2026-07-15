using System.Net;
using System.Text.Json;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;

namespace AssetBlock.WebApi.IntegrationTests.HealthChecks;

[Collection(nameof(IntegrationTestCollection))]
public sealed class HealthEndpointsIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Live_WhenProcessIsRunning_ShouldReturnHealthyJson()
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.GetAsync(new Uri("/health/live", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var document = await JsonDocument.ParseAsync(stream);
        document.RootElement.GetProperty("status").GetString().Should().Be("Healthy");
        document.RootElement.GetProperty("checks").TryGetProperty("self", out _).Should().BeTrue();
    }
}
