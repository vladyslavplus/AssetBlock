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

        var loginResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/login", UriKind.Relative),
            new LoginRequest(email, password));

        loginResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var loginTokens = await loginResponse.Content.ReadFromJsonAsync<IntegrationTestAuth.TokensResponseDto>(IntegrationTestAuth.JsonOptions);
        loginTokens.Should().NotBeNull();
        loginTokens.AccessToken.Should().NotBeNullOrWhiteSpace();
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
