using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class TagsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task SearchTags_WhenDefaults_ShouldReturnOkWithSeededItems()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/tags?page=1&pageSize=20", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        root.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("totalCount").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SearchTags_WithSearchTerm_ShouldReturnSeededMatch()
    {
        var client = fixture.Factory.CreateClient();
        var response = await client.GetAsync(new Uri("/api/tags?search=react&page=1&pageSize=50", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var page = await response.Content.ReadFromJsonAsync<PagedTagsResponse>();
        page.Should().NotBeNull();
        page.Items.Should().Contain(t => t.Name == "react");
    }

    [Fact]
    public async Task GetById_WhenTagMissing_ShouldReturnNotFoundWithErrorCode()
    {
        var client = fixture.Factory.CreateClient();
        var missingId = Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479");
        var response = await client.GetAsync(new Uri($"/api/tags/{missingId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("ERR_TAG_NOT_FOUND");
    }

    [Fact]
    public async Task GetById_WhenTagExists_ShouldReturnTag()
    {
        var client = fixture.Factory.CreateClient();
        var listResponse = await client.GetAsync(new Uri("/api/tags?page=1&pageSize=1&sortBy=name&sortDirection=ASC", UriKind.Relative));
        listResponse.EnsureSuccessStatusCode();
        var page = await listResponse.Content.ReadFromJsonAsync<PagedTagsResponse>();
        page.Should().NotBeNull();
        page.Items.Should().NotBeEmpty();

        var id = page.Items[0].Id;
        var response = await client.GetAsync(new Uri($"/api/tags/{id}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var tag = await response.Content.ReadFromJsonAsync<TagItemResponse>();
        tag.Should().NotBeNull();
        tag.Id.Should().Be(id);
        tag.Name.Should().NotBeNullOrWhiteSpace();
    }

    private sealed record PagedTagsResponse(IReadOnlyList<TagItemResponse> Items, int TotalCount, int Page, int PageSize);

    private sealed record TagItemResponse(Guid Id, string Name);
}
