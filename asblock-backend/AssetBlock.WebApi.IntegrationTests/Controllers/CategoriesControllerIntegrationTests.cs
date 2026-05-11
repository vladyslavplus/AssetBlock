using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class CategoriesControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task Get_WhenDefaults_ShouldReturnOkWithSeededItems()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/categories?page=1&pageSize=20", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("totalCount").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task Get_WithSearch_ShouldReturnMatchingSeededCategory()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/categories?search=Algorithms&page=1&pageSize=20", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedCategoriesResponse>();
        page.Should().NotBeNull();
        page.Items.Should().Contain(c => c.Name == "Algorithms");
    }

    [Fact]
    public async Task Get_WhenSortByInvalid_ShouldReturnBadRequest()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/categories?page=1&pageSize=10&sortBy=InvalidSort", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("SortBy");
    }

    [Fact]
    public async Task Get_WhenPageSizeTooLarge_ShouldReturnBadRequest()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/categories?page=1&pageSize=101", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("PageSize");
    }

    [Fact]
    public async Task GetById_WhenMissing_ShouldReturnNotFoundWithErrorCode()
    {
        var client = fixture.Factory.CreateClient();
        var missingId = Guid.Parse("c2d3e4f5-6a7b-8901-cdef-123456789abc");
        var response = await client.GetAsync(new Uri($"/api/categories/{missingId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("ERR_CATEGORY_NOT_FOUND");
    }

    [Fact]
    public async Task GetById_WhenExists_ShouldReturnCategory()
    {
        var client = fixture.Factory.CreateClient();
        var listResponse = await client.GetAsync(new Uri("/api/categories?page=1&pageSize=1&sortBy=name&sortDirection=ASC", UriKind.Relative));
        listResponse.EnsureSuccessStatusCode();
        var page = await listResponse.Content.ReadFromJsonAsync<PagedCategoriesResponse>();
        page.Should().NotBeNull();
        page.Items.Should().NotBeEmpty();

        var id = page.Items[0].Id;
        var response = await client.GetAsync(new Uri($"/api/categories/{id}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var detail = await response.Content.ReadFromJsonAsync<CategoryDetailResponse>();
        detail.Should().NotBeNull();
        detail.Id.Should().Be(id);
        detail.Name.Should().NotBeNullOrWhiteSpace();
        detail.Slug.Should().NotBeNullOrWhiteSpace();
    }

    private sealed record PagedCategoriesResponse(
        IReadOnlyList<CategoryListItemDto> Items,
        int TotalCount,
        int Page,
        int PageSize);

    private sealed record CategoryListItemDto(Guid Id, string Name, string Slug, string? Description);

    private sealed record CategoryDetailResponse(Guid Id, string Name, string Slug, string? Description);
}
