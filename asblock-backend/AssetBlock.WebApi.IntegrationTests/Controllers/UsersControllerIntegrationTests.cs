using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class UsersControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task ListSocialPlatforms_ShouldReturnOkWithSeededPlatforms()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/users/social-platforms", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("GitHub");
    }

    [Fact]
    public async Task GetMe_WithoutAuth_ShouldReturn401()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/users/me", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMe_WithAuth_ShouldReturnOk()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await client.GetAsync(new Uri("/api/users/me", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await response.Content.ReadFromJsonAsync<UserProfileDto>(IntegrationTestAuth.JsonOptions);
        profile.Should().NotBeNull();
        profile.Username.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetByUsername_WhenUserExists_ShouldReturnOk()
    {
        var (authClient, username) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        _ = authClient;

        var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync(new Uri($"/api/users/{Uri.EscapeDataString(username)}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var profile = await response.Content.ReadFromJsonAsync<UserProfileDto>(IntegrationTestAuth.JsonOptions);
        profile.Should().NotBeNull();
        profile.Username.Should().Be(username);
    }

    [Fact]
    public async Task ListNotifications_WithAuth_ShouldReturnOk()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await client.GetAsync(new Uri("/api/users/me/notifications?page=1&pageSize=10", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("items").Should().NotBeNull();
    }

    [Fact]
    public async Task MarkNotificationRead_WithAuth_WhenMissing_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var missingId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        var response = await client.PatchAsync(
            new Uri($"/api/users/me/notifications/{missingId}/read", UriKind.Relative),
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task UpdateMe_WithAuth_ShouldReturnOk()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await client.PatchAsJsonAsync(
            new Uri("/api/users/me", UriKind.Relative),
            new { bio = "Integration test bio." });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateSocials_WithAuth_EmptyLinks_ShouldReturnOk()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await client.PutAsJsonAsync(
            new Uri("/api/users/me/socials", UriKind.Relative),
            new UpdateUserSocialLinksRequest { Links = [] });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
