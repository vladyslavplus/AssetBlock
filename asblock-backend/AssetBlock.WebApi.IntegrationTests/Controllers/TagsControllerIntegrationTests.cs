using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class TagsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task SearchTags_WhenDefaults_ShouldReturnOkWithSeededItems()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/tags?page=1&pageSize=20", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        JsonElement root = doc.RootElement;
        root.GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);
        root.GetProperty("totalCount").GetInt32().Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task SearchTags_WithSearchTerm_ShouldReturnSeededMatch()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(new Uri("/api/tags?search=react&page=1&pageSize=50", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        PagedTagsResponse? page = await response.Content.ReadFromJsonAsync<PagedTagsResponse>();
        page.Should().NotBeNull();
        page.Items.Should().Contain(t => t.Name == "react");
    }

    [Fact]
    public async Task GetById_WhenTagMissing_ShouldReturnNotFoundWithErrorCode()
    {
        HttpClient client = fixture.Factory.CreateClient();
        var missingId = Guid.Parse("f47ac10b-58cc-4372-a567-0e02b2c3d479");
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/tags/{missingId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("ERR_TAG_NOT_FOUND");
    }

    [Fact]
    public async Task GetById_WhenTagExists_ShouldReturnTag()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage listResponse = await client.GetAsync(new Uri("/api/tags?page=1&pageSize=1&sortBy=name&sortDirection=ASC", UriKind.Relative));
        listResponse.EnsureSuccessStatusCode();
        PagedTagsResponse? page = await listResponse.Content.ReadFromJsonAsync<PagedTagsResponse>();
        page.Should().NotBeNull();
        page.Items.Should().NotBeEmpty();

        Guid id = page.Items[0].Id;
        HttpResponseMessage response = await client.GetAsync(new Uri($"/api/tags/{id}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        TagItemResponse? tag = await response.Content.ReadFromJsonAsync<TagItemResponse>();
        tag.Should().NotBeNull();
        tag.Id.Should().Be(id);
        tag.Name.Should().NotBeNullOrWhiteSpace();
    }

    private sealed record PagedTagsResponse(IReadOnlyList<TagItemResponse> Items, int TotalCount, int Page, int PageSize);

    private sealed record TagItemResponse(Guid Id, string Name);
}
