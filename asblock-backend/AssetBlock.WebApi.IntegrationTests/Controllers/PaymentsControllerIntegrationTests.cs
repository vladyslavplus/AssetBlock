using System.Net;
using System.Net.Http.Json;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class PaymentsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task CreateCheckout_WithoutAuth_ShouldReturn401()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            new Uri("/api/payments/checkout", UriKind.Relative),
            new CreateCheckoutRequest(Guid.NewGuid()));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateCheckout_WithAuth_WhenAssetMissing_ShouldReturnBadRequest()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await client.PostAsJsonAsync(
            new Uri("/api/payments/checkout", UriKind.Relative),
            new CreateCheckoutRequest(Guid.Parse("d4e5f6a7-b8c9-0123-def0-456789abcdef")));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("ERR_ASSET_NOT_FOUND");
    }

    [Fact]
    public async Task Webhook_WithInvalidPayload_ShouldReturnOk()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsync(
            new Uri("/api/payments/webhook", UriKind.Relative),
            new StringContent("{}", System.Text.Encoding.UTF8, "application/json"));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
