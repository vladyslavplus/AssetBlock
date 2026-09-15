using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class PersonalSimilarAssetsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task GetPersonalSimilar_WhenAnonymous_ShouldReturnUnauthorized()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/users/me/assets/{Guid.NewGuid()}/similar", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetRecommendationPreferences_WhenAnonymous_ShouldReturnUnauthorized()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/users/me/recommendation-preferences", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task UpdateRecommendationPreferences_WhenAnonymous_ShouldReturnUnauthorized()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await client.PatchAsJsonAsync(
            new Uri("/api/users/me/recommendation-preferences", UriKind.Relative),
            new { isPersonalized = true });

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task GetRecommendationPreferences_WhenNeverSaved_ShouldReturnDefaultOff()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);

        HttpResponseMessage response = await client.GetAsync(
            new Uri("/api/users/me/recommendation-preferences", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        PreferencesResponse? payload = await response.Content.ReadFromJsonAsync<PreferencesResponse>();
        payload.Should().NotBeNull();
        payload!.IsPersonalized.Should().BeFalse();
        payload.OptedInAt.Should().BeNull();
    }

    [Fact]
    public async Task UpdateRecommendationPreferences_WhenOptIn_ShouldPersistAndReflect()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);

        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            new Uri("/api/users/me/recommendation-preferences", UriKind.Relative),
            new { isPersonalized = true });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);
        PreferencesResponse? saved = await patch.Content.ReadFromJsonAsync<PreferencesResponse>();
        saved.Should().NotBeNull();
        saved!.IsPersonalized.Should().BeTrue();
        saved.OptedInAt.Should().NotBeNull();

        HttpResponseMessage get = await client.GetAsync(
            new Uri("/api/users/me/recommendation-preferences", UriKind.Relative));
        PreferencesResponse? fetched = await get.Content.ReadFromJsonAsync<PreferencesResponse>();
        fetched.Should().NotBeNull();
        fetched!.IsPersonalized.Should().BeTrue();
    }

    [Fact]
    public async Task GetPersonalSimilar_WhenOptedOut_ShouldReturnPhaseAVersion()
    {
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        (Guid sourceId, Guid peerId) = await SimilarAssetsSeed.EnsurePublicPairAsync(scopeFactory);
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);

        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/users/me/assets/{sourceId}/similar?limit=6", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SimilarAssetsResponse? payload = await response.Content.ReadFromJsonAsync<SimilarAssetsResponse>();
        payload.Should().NotBeNull();
        payload!.Items.Should().ContainSingle(i => i.Id == peerId);
        payload.Exposure.Should().NotBeNull();
        payload.Exposure!.RankingVersion.Should().Be("similar-assets-v1-metadata");
    }

    [Fact]
    public async Task GetPersonalSimilar_WhenOptedInWithoutSignals_ShouldReturnPersonalVersionWithPhaseAOrder()
    {
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        (Guid sourceId, Guid peerId) = await SimilarAssetsSeed.EnsurePublicPairAsync(scopeFactory);
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);

        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            new Uri("/api/users/me/recommendation-preferences", UriKind.Relative),
            new { isPersonalized = true });
        patch.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/users/me/assets/{sourceId}/similar?limit=6", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        SimilarAssetsResponse? payload = await response.Content.ReadFromJsonAsync<SimilarAssetsResponse>();
        payload.Should().NotBeNull();
        payload!.Items.Should().ContainSingle(i => i.Id == peerId);
        payload.Exposure.Should().NotBeNull();
        payload.Exposure!.RankingVersion.Should().Be("similar-assets-v1-personal");
        payload.Exposure.Token.Should().HaveLength(64);
        // Opted in but signal-empty: personal version with Phase A order and no personal reason.
        payload.Explanations.Should().ContainSingle();
        payload.Explanations![0].AssetId.Should().Be(peerId);
        payload.Explanations[0].Code.Should().NotBe("PERSONAL_RECOMMENDATION_CHOICES");
        payload.Explanations[0].Code.Should().NotBe("PERSONAL_TAG_INTERESTS");
    }

    [Fact]
    public async Task GetPersonalSimilar_WhenModeUnknown_ShouldReturnBadRequest()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);

        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/users/me/assets/{Guid.NewGuid()}/similar?mode=trending", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await response.Content.ReadAsStringAsync();
        body.Should().Contain("Mode");
    }

    [Fact]
    public async Task GetPersonalSimilar_WhenSourceMissing_ShouldReturnNotFound()
    {
        (HttpClient client, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        var missingId = Guid.Parse("b1e2d3c4-5a6b-7890-abcd-ef1234567899");

        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/users/me/assets/{missingId}/similar", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var json = await response.Content.ReadAsStringAsync();
        json.Should().Contain("ERR_ASSET_NOT_FOUND");
    }

    private sealed record PreferencesResponse(bool IsPersonalized, DateTimeOffset? OptedInAt);

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
