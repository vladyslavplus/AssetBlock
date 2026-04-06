using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class ReviewsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task GetReviews_WhenDefaults_ShouldReturnOk()
    {
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var assetId = await AssetCatalogSeed.EnsureSampleAssetAsync(scopeFactory);

        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(
            new Uri($"/api/reviews/assets/{assetId}/reviews?page=1&pageSize=10", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task GetReviews_WhenSortByInvalid_ShouldReturnBadRequest()
    {
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var assetId = await AssetCatalogSeed.EnsureSampleAssetAsync(scopeFactory);

        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(
            new Uri($"/api/reviews/assets/{assetId}/reviews?page=1&pageSize=10&sortBy=Invalid", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("SortBy");
    }

    [Fact]
    public async Task GetReviewById_WhenMissing_ShouldReturnNotFound()
    {
        var client = fixture.Factory.CreateClient();
        var missingId = Guid.Parse("e5f6a7b8-c9d0-1234-ef01-23456789abcd");
        var response = await client.GetAsync(new Uri($"/api/reviews/reviews/{missingId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("ERR_REVIEW_NOT_FOUND");
    }

    [Fact]
    public async Task CreateReview_WithoutAuth_ShouldReturn401()
    {
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var assetId = await AssetCatalogSeed.EnsureSampleAssetAsync(scopeFactory);

        var client = fixture.Factory.CreateClient();
        var response = await client.PostAsJsonAsync(
            new Uri($"/api/reviews/assets/{assetId}/reviews", UriKind.Relative),
            new { rating = 5, comment = "Great" });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteReview_WithoutAuth_ShouldReturn401()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.DeleteAsync(new Uri($"/api/reviews/reviews/{Guid.NewGuid()}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task DeleteReview_AsNonAdmin_ShouldReturn403()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var response = await client.DeleteAsync(new Uri($"/api/reviews/reviews/{Guid.NewGuid()}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
