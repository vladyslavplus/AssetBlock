using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class AssetsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task List_WhenDefaults_ShouldReturnOk()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/assets?page=1&pageSize=10", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        _ = doc.RootElement.GetProperty("items");
        doc.RootElement.GetProperty("totalCount").GetInt32().Should().BeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public async Task List_WhenSortByInvalid_ShouldReturnBadRequest()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/assets?page=1&pageSize=10&sortBy=InvalidSort", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("SortBy");
    }

    [Fact]
    public async Task List_WhenPageSizeTooLarge_ShouldReturnBadRequest()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/assets?page=1&pageSize=101", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("PageSize");
    }

    [Fact]
    public async Task List_WhenMinPriceAboveMaxPrice_ShouldReturnBadRequest()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/assets?page=1&pageSize=10&minPrice=10&maxPrice=5", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("MinPrice");
    }

    [Fact]
    public async Task GetById_WhenMissing_ShouldReturnNotFoundWithErrorCode()
    {
        var client = fixture.Factory.CreateClient();
        var missingId = Guid.Parse("b1e2d3c4-5a6b-7890-abcd-ef1234567890");
        var response = await client.GetAsync(new Uri($"/api/assets/{missingId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("ERR_ASSET_NOT_FOUND");
    }

    [Fact]
    public async Task GetById_WhenExists_ShouldReturnDetail()
    {
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var assetId = await AssetCatalogSeed.EnsureSampleAssetAsync(scopeFactory);

        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri($"/api/assets/{assetId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<AssetDetailResponse>();
        detail.Should().NotBeNull();
        detail.Id.Should().Be(assetId);
        detail.Title.Should().Be("Integration seeded asset");
        detail.Price.Should().Be(9.99m);
        detail.CategoryName.Should().NotBeNullOrWhiteSpace();
    }

    private sealed record AssetDetailResponse(
        Guid Id,
        string Title,
        string? Description,
        decimal Price,
        Guid CategoryId,
        string? CategoryName,
        Guid AuthorId,
        DateTimeOffset CreatedAt,
        DateTimeOffset? UpdatedAt);
}
