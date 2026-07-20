using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;

namespace AssetBlock.WebApi.IntegrationTests.ProblemDetails;

[Collection(nameof(IntegrationTestCollection))]
public sealed class ErrorContractPipelineIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task JwtChallenge_WhenMissingBearer_ShouldReturn401ProblemDetails()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/users/me", UriKind.Relative));

        await AssertProblemAsync(response, HttpStatusCode.Unauthorized, ErrorCodes.ERR_AUTH_TOKEN_INVALID);
    }

    [Fact]
    public async Task JwtForbidden_WhenUserLacksAdminRole_ShouldReturn403ProblemDetails()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await client.PostAsJsonAsync(
            new Uri("/api/tags", UriKind.Relative),
            new { name = $"tag_{Guid.NewGuid():N}" });

        await AssertProblemAsync(response, HttpStatusCode.Forbidden, ErrorCodes.ERR_FORBIDDEN);
    }

    [Fact]
    public async Task ModelBinding_WhenBodyInvalid_ShouldReturnValidationProblemDetails()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        using var content = new StringContent(
            """{"assetId":"not-a-guid"}""",
            Encoding.UTF8,
            "application/json");
        var response = await client.PostAsync(new Uri("/api/payments/checkout", UriKind.Relative), content);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ERR_VALIDATION_FAILED);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("errors");
    }

    [Fact]
    public async Task ExceptionHandler_WhenFluentValidationFails_ShouldReturnValidationProblemDetails()
    {
        var client = fixture.Factory.CreateClient();
        var suffix = Guid.NewGuid().ToString("N");
        var response = await client.PostAsJsonAsync(
            new Uri("/api/auth/register", UriKind.Relative),
            new { username = $"w_{suffix}", email = $"w-{suffix}@test.local", password = "short" });

        await AssertProblemAsync(response, HttpStatusCode.BadRequest, ErrorCodes.ERR_VALIDATION_FAILED);
    }

    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        response.StatusCode.Should().Be(status);
        response.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        await using var stream = await response.Content.ReadAsStreamAsync();
        using var doc = await JsonDocument.ParseAsync(stream);
        var root = doc.RootElement;
        root.GetProperty("status").GetInt32().Should().Be((int)status);
        root.GetProperty("type").GetString().Should().Be($"urn:assetblock:error:{code}");
        root.GetProperty("code").GetString().Should().Be(code);
        root.TryGetProperty("traceId", out _).Should().BeTrue();
    }
}
