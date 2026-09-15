using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class SimilarAssetsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task GetSimilar_WhenLimitInvalid_ShouldReturnBadRequest()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/assets/{Guid.NewGuid()}/similar?limit=0", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Limit");
    }

    [Fact]
    public async Task GetSimilar_WhenModeUnknown_ShouldReturnBadRequest()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/assets/{Guid.NewGuid()}/similar?mode=trending", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Mode");
    }

    [Fact]
    public async Task GetSimilar_WhenSourceMissing_ShouldReturnNotFound()
    {
        HttpClient client = fixture.Factory.CreateClient();
        var missingId = Guid.Parse("b1e2d3c4-5a6b-7890-abcd-ef1234567899");
        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/assets/{missingId}/similar", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("ERR_ASSET_NOT_FOUND");
    }

    [Fact]
    public async Task GetSimilar_WhenVisibleWithoutCandidates_ShouldReturnEmptyItems()
    {
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        Guid assetId = await SimilarAssetsSeed.EnsureEmptySourceAsync(scopeFactory);

        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/assets/{assetId}/similar", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        doc.RootElement.GetProperty("items").GetArrayLength().Should().Be(0);
        doc.RootElement.TryGetProperty("exposure", out JsonElement exposure).Should().BeTrue();
        exposure.ValueKind.Should().Be(JsonValueKind.Null);
        doc.RootElement.TryGetProperty("explanations", out JsonElement explanations).Should().BeTrue();
        explanations.GetArrayLength().Should().Be(0);
        doc.RootElement.TryGetProperty("totalCount", out _).Should().BeFalse();
        doc.RootElement.TryGetProperty("isTruncated", out _).Should().BeFalse();
    }

    [Fact]
    public async Task GetSimilar_WhenPublicCandidatesExist_ShouldReturnPublicCardPayload()
    {
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        (Guid sourceId, Guid peerId) = await SimilarAssetsSeed.EnsurePublicPairAsync(scopeFactory);

        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/assets/{sourceId}/similar?limit=6", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SimilarAssetsResponse? payload = await response.Content.ReadFromJsonAsync<SimilarAssetsResponse>();
        payload.Should().NotBeNull();
        payload.Items.Should().ContainSingle(i => i.Id == peerId);
        payload.Exposure.Should().NotBeNull();
        payload.Exposure!.RankingVersion.Should().Be("similar-assets-v1-metadata");
        payload.Exposure.Token.Should().HaveLength(64);
        payload.Exposure.Id.Should().NotBe(Guid.Empty);
        SimilarAssetItemResponse item = payload.Items[0];
        item.Title.Should().Be("Similar pair peer");
        item.AuthorUsername.Should().NotBeNullOrWhiteSpace();
        item.Tags.Should().NotBeNull();
        payload.Explanations.Should().ContainSingle();
        payload.Explanations![0].AssetId.Should().Be(peerId);
        payload.Explanations[0].Code.Should().NotBeNullOrWhiteSpace();
        payload.Explanations[0].Text.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task GetSimilar_WhenPopularityMode_ShouldReturnPopularityExposure()
    {
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        (Guid sourceId, Guid peerId) = await SimilarAssetsSeed.EnsurePublicPairAsync(scopeFactory);

        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/assets/{sourceId}/similar?limit=6&mode=popularity", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SimilarAssetsResponse? payload = await response.Content.ReadFromJsonAsync<SimilarAssetsResponse>();
        payload.Should().NotBeNull();
        payload.Items.Should().ContainSingle(i => i.Id == peerId);
        payload.Exposure.Should().NotBeNull();
        payload.Exposure!.RankingVersion.Should().Be("similar-assets-v1-popularity");
        payload.Exposure.Token.Should().HaveLength(64);
    }

    private sealed record SimilarAssetsResponse(
        IReadOnlyList<SimilarAssetItemResponse> Items,
        SimilarAssetsExposureResponse? Exposure,
        IReadOnlyList<SimilarAssetExplanationResponse>? Explanations);

    private sealed record SimilarAssetExplanationResponse(
        Guid AssetId,
        string Code,
        string Text);

    private sealed record SimilarAssetsExposureResponse(
        Guid Id,
        string RankingVersion,
        DateTimeOffset ExpiresAt,
        string Token);

    private sealed record SimilarAssetItemResponse(
        Guid Id,
        string Title,
        string? Description,
        decimal Price,
        Guid CategoryId,
        string? CategoryName,
        Guid AuthorId,
        string AuthorUsername,
        DateTimeOffset CreatedAt,
        IReadOnlyList<string> Tags,
        double AverageRating);
}
