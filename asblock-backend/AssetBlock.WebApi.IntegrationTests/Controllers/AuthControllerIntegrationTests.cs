using System.Net;
using System.Net.Http.Json;
using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class AuthControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Login_WithUnknownEmail_ShouldReturnBadRequest()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequest("nonexistent-integ@test.local", "Password1!"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ERR_AUTH_INVALID_CREDENTIALS");
    }

    [Fact]
    public async Task RegisterThenLogin_ShouldReturnOkWithTokens()
    {
        var client = fixture.Factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"integ-{suffix}@test.local";
        const string password = "Password1!";

        var registerResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new RegisterRequest($"user_{suffix}", email, password));

        registerResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var registerTokens = await registerResponse.Content.ReadFromJsonAsync<IntegrationTestAuth.TokensResponseDto>(IntegrationTestAuth.JsonOptions);
        registerTokens.Should().NotBeNull();
        registerTokens.AccessToken.Should().NotBeNullOrWhiteSpace();
        registerTokens.RefreshToken.Should().NotBeNullOrWhiteSpace();

        client.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", registerTokens.AccessToken);
        var meResponse = await client.GetAsync(new Uri("/api/users/me", UriKind.Relative));
        meResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var meJson = await meResponse.Content.ReadAsStringAsync();
        meJson.Should().Contain("\"emailVerifiedAt\":null");

        client.DefaultRequestHeaders.Authorization = null;
        var loginResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequest(email, password));

        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginTokens = await loginResponse.Content.ReadFromJsonAsync<IntegrationTestAuth.TokensResponseDto>(IntegrationTestAuth.JsonOptions);
        loginTokens.Should().NotBeNull();
        loginTokens.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task PasswordResetRequest_ForKnownAndUnknownEmail_ShouldReturnAcceptedIndistinguishably()
    {
        var client = fixture.Factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N");
        var knownEmail = $"reset-known-{suffix}@test.local";
        const string password = "Password1!";

        var registerResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new RegisterRequest($"reset_{suffix}", knownEmail, password));
        registerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var knownResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/password-reset/request", UriKind.Relative),
            new RequestPasswordResetRequest(knownEmail));
        var unknownResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/password-reset/request", UriKind.Relative),
            new RequestPasswordResetRequest($"reset-unknown-{suffix}@test.local"));

        knownResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
        unknownResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);

        var knownBody = await knownResponse.Content.ReadAsStringAsync();
        var unknownBody = await unknownResponse.Content.ReadAsStringAsync();
        knownBody.Should().Be(unknownBody);
    }

    [Fact]
    public async Task ConfirmEmailVerification_WithGarbageToken_ShouldReturnGenericInvalid()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/email-verification/confirm", UriKind.Relative),
            new ConfirmEmailActionRequest("not-a-real-token"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ERR_EMAIL_ACTION_INVALID_OR_EXPIRED");
        body.Contains("token=", StringComparison.OrdinalIgnoreCase).Should().BeFalse();
    }

    [Fact]
    public async Task Register_WithWeakPassword_ShouldReturnBadRequest()
    {
        var client = fixture.Factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new RegisterRequest($"weak_{suffix}", $"weak-{suffix}@test.local", "short"));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
