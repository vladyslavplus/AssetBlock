using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Authorization;

[Collection(nameof(IntegrationTestCollection))]
public sealed class VerifiedEmailPolicyIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task ProtectedEndpoints_WhenUnverified_ShouldReturn403EmailNotVerified()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var assetId = Guid.Parse("d4e5f6a7-b8c9-0123-def0-456789abcdef");

        await AssertEmailNotVerifiedAsync(
            await client.PostAsync(
                new Uri("/api/assets/upload", UriKind.Relative),
                new MultipartFormDataContent()));

        await AssertEmailNotVerifiedAsync(
            await client.PostAsJsonAsync(
                new Uri("/api/payments/checkout", UriKind.Relative),
                new CreateCheckoutRequest(assetId)));

        await AssertEmailNotVerifiedAsync(
            await client.PostAsJsonAsync(
                new Uri($"/api/reviews/assets/{assetId}/reviews", UriKind.Relative),
                new { rating = 5, comment = "x" }));

        await AssertEmailNotVerifiedAsync(
            await client.PatchAsJsonAsync(
                new Uri("/api/users/me", UriKind.Relative),
                new { bio = "blocked" }));

        await AssertEmailNotVerifiedAsync(
            await client.PutAsJsonAsync(
                new Uri("/api/users/me/socials", UriKind.Relative),
                new { links = Array.Empty<object>() }));
    }

    [Fact]
    public async Task AdminEndpoint_WhenUnverifiedAdmin_ShouldReturn403EmailNotVerified()
    {
        var (_, username) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);

        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var user = await db.Users.SingleAsync(u => u.Username == username);
            user.Role = AppRoles.ADMIN;
            await db.SaveChangesAsync();
        }

        var client = fixture.Factory.CreateClient();
        var email = await FindEmailAsync(username);
        var login = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequest(email, "Password1!"));
        login.EnsureSuccessStatusCode();
        var tokens = await login.Content.ReadFromJsonAsync<IntegrationTestAuth.TokensResponseDto>(
            IntegrationTestAuth.JsonOptions);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", tokens!.AccessToken);

        await AssertEmailNotVerifiedAsync(
            await client.GetAsync(new Uri("/api/admin/audit-logs?page=1&pageSize=5", UriKind.Relative)));
    }

    [Fact]
    public async Task ProtectedEndpoint_WhenVerified_ShouldPassAuthorizationBoundary()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var response = await client.PostAsJsonAsync(
            new Uri("/api/payments/checkout", UriKind.Relative),
            new CreateCheckoutRequest(Guid.Parse("d4e5f6a7-b8c9-0123-def0-456789abcdef")));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ERR_ASSET_NOT_FOUND");
        body.Should().NotContain(ErrorCodes.ERR_EMAIL_NOT_VERIFIED);
    }

    [Fact]
    public async Task AllowedLifecycleEndpoints_WhenUnverified_ShouldRemainAvailable()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);

        var me = await client.GetAsync(new Uri("/api/users/me", UriKind.Relative));
        me.StatusCode.Should().Be(HttpStatusCode.OK);

        var resend = await client.PostAsync(
            new Uri("/api/users/me/email-verification/resend", UriKind.Relative),
            new StringContent(string.Empty, Encoding.UTF8, "application/json"));
        resend.StatusCode.Should().BeOneOf(HttpStatusCode.OK, HttpStatusCode.BadRequest);

        var password = await client.PostAsJsonAsync(
            new Uri("/api/users/me/password", UriKind.Relative),
            new { currentPassword = "Password1!", newPassword = "Password2!" });
        password.StatusCode.Should().Be(HttpStatusCode.OK);

        var emailChange = await client.PostAsJsonAsync(
            new Uri("/api/users/me/email-change/request", UriKind.Relative),
            new { newEmail = $"new-{Guid.NewGuid():N}@test.local", currentPassword = "Password2!" });
        emailChange.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private static async Task AssertEmailNotVerifiedAsync(HttpResponseMessage response)
    {
        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");
        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;
        root.GetProperty("status").GetInt32().Should().Be(403);
        root.GetProperty("code").GetString().Should().Be(ErrorCodes.ERR_EMAIL_NOT_VERIFIED);
        root.GetProperty("type").GetString().Should().Be($"urn:assetblock:error:{ErrorCodes.ERR_EMAIL_NOT_VERIFIED}");
        root.TryGetProperty("traceId", out _).Should().BeTrue();
    }

    private async Task<string> FindEmailAsync(string username)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var user = await db.Users.AsNoTracking().SingleAsync(u => u.Username == username);
        return user.Email;
    }
}
