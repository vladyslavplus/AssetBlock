using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class UsersControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task ListSocialPlatforms_ShouldReturnOkWithSeededPlatforms()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/users/social-platforms", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("GitHub");
    }

    [Fact]
    public async Task GetMe_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/users/me", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMe_WithAuth_ShouldReturnOk()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/users/me", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        UserProfileDto? profile = await response.Content.ReadFromJsonAsync<UserProfileDto>(IntegrationTestAuth.JsonOptions);
        profile.Should().NotBeNull();
        profile.Username.Should().NotBeNullOrWhiteSpace();
        profile.Role.Should().Be(AppRoles.USER);
    }

    [Fact]
    public async Task GetByUsername_WhenUserExists_ShouldReturnOk()
    {
        (HttpClient authClient, var username) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        _ = authClient;

        HttpClient anonymous = fixture.Factory.CreateClient();
        HttpResponseMessage response = await anonymous.GetAsync(new Uri($"/api/users/{Uri.EscapeDataString(username)}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        UserProfileDto? profile = await response.Content.ReadFromJsonAsync<UserProfileDto>(IntegrationTestAuth.JsonOptions);
        profile.Should().NotBeNull();
        profile.Username.Should().Be(username);
    }

    [Fact]
    public async Task ListNotifications_WithAuth_ShouldReturnOk()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/users/me/notifications?page=1&pageSize=10", UriKind.Relative));

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
        HttpResponseMessage response = await client.PatchAsync(
            new Uri($"/api/users/me/notifications/{missingId}/read", UriKind.Relative),
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MarkNotificationUnread_WithAuth_WhenMissing_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var missingId = Guid.Parse("a1b2c3d4-e5f6-7890-abcd-ef1234567890");
        HttpResponseMessage response = await client.PatchAsync(
            new Uri($"/api/users/me/notifications/{missingId}/unread", UriKind.Relative),
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MarkAllNotificationsRead_WithAuth_ShouldReturnOkWithCount()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.PostAsync(
            new Uri("/api/users/me/notifications/read-all", UriKind.Relative),
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("updatedCount");
    }

    [Fact]
    public async Task UpdateMe_WithAuth_ShouldReturnOk()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.PatchAsJsonAsync(
            new Uri("/api/users/me", UriKind.Relative),
            new { bio = "Integration test bio." });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task UpdateSocials_WithAuth_EmptyLinks_ShouldReturnOk()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.PutAsJsonAsync(
            new Uri("/api/users/me/socials", UriKind.Relative),
            new UpdateUserSocialLinksRequest { Links = [] });

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task GetMyAssetProcessingJobs_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/users/me/assets/{Guid.NewGuid()}/processing-jobs", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMyAssetProcessingJobs_WithAuth_WhenAssetNotFound_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/users/me/assets/{Guid.NewGuid()}/processing-jobs", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMyAssetVersionProcessingJobs_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/users/me/asset-versions/{Guid.NewGuid()}/processing-jobs", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMyAssetVersionProcessingJobs_WithAuth_WhenVersionNotFound_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/users/me/asset-versions/{Guid.NewGuid()}/processing-jobs", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetMyAsset_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/users/me/assets/{Guid.NewGuid()}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetMyAsset_WithAuth_WhenMissing_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/users/me/assets/{Guid.NewGuid()}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListingCopilot_WithoutAuth_ShouldReturn401()
    {
        HttpClient client = fixture.Factory.CreateClient();
        var versionId = Guid.NewGuid();
        HttpResponseMessage get = await client.GetAsync(new Uri($"/api/users/me/asset-versions/{versionId}/listing-copilot", UriKind.Relative));
        HttpResponseMessage post = await client.PostAsync(new Uri($"/api/users/me/asset-versions/{versionId}/listing-copilot", UriKind.Relative), null);

        get.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        post.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task ListingCopilotPost_WhenEmailUnverified_ShouldReturn403()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        HttpResponseMessage response = await client.PostAsync(
            new Uri($"/api/users/me/asset-versions/{Guid.NewGuid()}/listing-copilot", UriKind.Relative),
            null);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task ListingCopilot_WithAuth_WhenVersionMissing_ShouldReturn404()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var versionId = Guid.NewGuid();
        HttpResponseMessage get = await client.GetAsync(new Uri($"/api/users/me/asset-versions/{versionId}/listing-copilot", UriKind.Relative));
        HttpResponseMessage post = await client.PostAsync(new Uri($"/api/users/me/asset-versions/{versionId}/listing-copilot", UriKind.Relative), null);

        get.StatusCode.Should().Be(HttpStatusCode.NotFound);
        post.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
