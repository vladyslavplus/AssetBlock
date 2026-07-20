using System.Net;
using System.Net.Http.Json;
using AssetBlock.Domain.Core.Dto.Auth;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

/// <summary>
/// Password-reset request returns 202 for known and unknown emails (anti-enumeration).
/// Register paths require the email_actions migration.
/// </summary>
[Collection(nameof(IntegrationTestCollection))]
public sealed class PasswordResetControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task RequestPasswordReset_WhenEmailUnknown_ShouldReturn202()
    {
        var client = fixture.Factory.CreateClient();

        var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/password-reset/request", UriKind.Relative),
            new RequestPasswordResetRequest($"unknown-{Guid.NewGuid():N}@nowhere.test"));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task RequestPasswordReset_WhenEmailKnown_ShouldReturn202()
    {
        var client = fixture.Factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"pwreset-{suffix}@test.local";
        const string password = "Password1!";

        var registerResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new RegisterRequest($"pwreset_{suffix}", email, password));
        registerResponse.StatusCode.Should().Be(HttpStatusCode.OK);

        var resetResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/password-reset/request", UriKind.Relative),
            new RequestPasswordResetRequest(email));

        resetResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task RequestPasswordReset_ShouldReturnSameStatusForKnownAndUnknown()
    {
        var client = fixture.Factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N");
        var email = $"pwreset2-{suffix}@test.local";
        const string password = "Password1!";

        await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new RegisterRequest($"pwreset2_{suffix}", email, password));

        var knownResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/password-reset/request", UriKind.Relative),
            new RequestPasswordResetRequest(email));

        var unknownResponse = await client.PostAsJsonAsync(
            new Uri("/api/auth/password-reset/request", UriKind.Relative),
            new RequestPasswordResetRequest($"unknown-{Guid.NewGuid():N}@nowhere.test"));

        knownResponse.StatusCode.Should().Be(unknownResponse.StatusCode,
            because: "anti-enumeration: both known and unknown emails return the same HTTP status");
        knownResponse.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }
}
