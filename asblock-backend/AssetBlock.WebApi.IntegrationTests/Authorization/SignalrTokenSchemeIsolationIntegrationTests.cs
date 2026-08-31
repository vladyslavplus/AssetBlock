using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;

namespace AssetBlock.WebApi.IntegrationTests.Authorization;

[Collection(nameof(IntegrationTestCollection))]
public sealed class SignalrTokenSchemeIsolationIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task SignalrTokenEndpoint_WhenAnonymous_ShouldReturn401Unauthorized()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/auth/signalr-token", UriKind.Relative),
            null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task SignalrTokenEndpoint_WhenAuthenticatedWithApiBearer_ShouldReturnHubToken()
    {
        var (client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);

        var response = await client.PostAsync(
            new Uri("/api/auth/signalr-token", UriKind.Relative),
            null);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var hubTokenDto = await response.Content.ReadFromJsonAsync<HubTokenResponse>(IntegrationTestAuth.JsonOptions);

        hubTokenDto.Should().NotBeNull();
        hubTokenDto.HubToken.Should().NotBeNullOrWhiteSpace();
        hubTokenDto.ExpiresAt.Should().BeAfter(DateTimeOffset.UtcNow);
    }

    [Fact]
    public async Task RestApiEndpoint_WhenPresentedWithHubToken_ShouldReturn401Unauthorized()
    {
        var (client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);

        var tokenResponse = await client.PostAsync(
            new Uri("/api/auth/signalr-token", UriKind.Relative),
            null);
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var hubTokenDto = await tokenResponse.Content.ReadFromJsonAsync<HubTokenResponse>(IntegrationTestAuth.JsonOptions);

        // Create a new client presenting the hub token to REST API endpoints
        var hubClient = fixture.Factory.CreateClient();
        hubClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", hubTokenDto!.HubToken);

        // REST endpoint requiring API bearer scheme must reject hub token
        var meResponse = await hubClient.GetAsync(new Uri("/api/users/me", UriKind.Relative));
        meResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var signalrTokenEndpointResponse = await hubClient.PostAsync(
            new Uri("/api/auth/signalr-token", UriKind.Relative),
            null);
        signalrTokenEndpointResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NotificationsHubNegotiate_WhenAnonymous_ShouldReturn401Unauthorized()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/hubs/notifications/negotiate?negotiateVersion=1", UriKind.Relative),
            null);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NotificationsHubNegotiate_WhenPresentedWithStandardApiToken_ShouldReturn401Unauthorized()
    {
        var (apiClient, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var apiBearerToken = apiClient.DefaultRequestHeaders.Authorization!.Parameter!;

        // Attempting to negotiate SignalR hub with standard API token (either query param or header) must be rejected
        var client = fixture.Factory.CreateClient();

        // 1. Query parameter token
        var queryResponse = await client.PostAsync(
            new Uri($"/hubs/notifications/negotiate?negotiateVersion=1&access_token={apiBearerToken}", UriKind.Relative),
            null);
        queryResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // 2. Authorization header token
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiBearerToken);
        var headerResponse = await client.PostAsync(
            new Uri("/hubs/notifications/negotiate?negotiateVersion=1", UriKind.Relative),
            null);
        headerResponse.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task NotificationsHubNegotiate_WhenPresentedWithValidHubToken_ShouldSucceed()
    {
        var (apiClient, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);

        var tokenResponse = await apiClient.PostAsync(
            new Uri("/api/auth/signalr-token", UriKind.Relative),
            null);
        tokenResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var hubTokenDto = await tokenResponse.Content.ReadFromJsonAsync<HubTokenResponse>(IntegrationTestAuth.JsonOptions);

        var client = fixture.Factory.CreateClient();

        // Negotiate via query parameter access_token
        var queryResponse = await client.PostAsync(
            new Uri($"/hubs/notifications/negotiate?negotiateVersion=1&access_token={hubTokenDto!.HubToken}", UriKind.Relative),
            null);

        queryResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var content = await queryResponse.Content.ReadAsStringAsync();
        content.Should().Contain("connectionId");
    }
}
