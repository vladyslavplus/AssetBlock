using System.Net;
using System.Net.Http.Json;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class RecommendationEventsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    private static readonly Uri _eventsUri = new("/api/analytics/recommendation-events", UriKind.Relative);
    private const string TEST_CLIENT_IP = "198.51.100.42";

    [Fact]
    public async Task IngestRecommendationEvent_WhenCandidateIdsMissing_Returns400()
    {
        HttpClient client = fixture.Factory.CreateClient();
        HttpResponseMessage response = await PostEventAsync(client, new
        {
            eventId = Guid.NewGuid(),
            eventType = "IMPRESSION",
            visitorId = Guid.NewGuid(),
            sessionId = Guid.NewGuid(),
            sourceAssetId = Guid.NewGuid(),
            targetAssetId = Guid.NewGuid(),
            slotPosition = 0,
            exposureId = Guid.NewGuid(),
            rankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
            expiresAt = DateTimeOffset.UtcNow.AddMinutes(10),
            exposureToken = new string('a', 64),
            deviceClass = "DESKTOP"
        });

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_RECOMMENDATION_EVENT_INVALID);
    }

    [Fact]
    public async Task IngestRecommendationEvent_WhenExposureIsValid_Returns202AndStoresEvent()
    {
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        (Guid sourceId, Guid peerId) = await SimilarAssetsSeed.EnsurePublicPairAsync(scopeFactory);
        HttpClient client = fixture.Factory.CreateClient();
        SimilarAssetsResponse similar = await GetSimilar(client, sourceId);

        var eventId = Guid.NewGuid();
        HttpResponseMessage response = await PostEventAsync(client, Payload(eventId, sourceId, peerId, similar.Exposure!));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        RecommendationEvent? stored = await GetStoredEvent(eventId);
        stored.Should().NotBeNull();
        stored.TargetAssetId.Should().Be(peerId);
        stored.ActorUserId.Should().BeNull();
        stored.EventType.Should().Be(RecommendationEventType.IMPRESSION);
    }

    [Fact]
    public async Task IngestRecommendationEvent_WhenTokenIsForged_Returns202WithoutStoring()
    {
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        (Guid sourceId, Guid peerId) = await SimilarAssetsSeed.EnsurePublicPairAsync(scopeFactory);
        HttpClient client = fixture.Factory.CreateClient();
        SimilarAssetsResponse similar = await GetSimilar(client, sourceId);
        SimilarAssetsExposureResponse exposure = similar.Exposure! with
        {
            Token = similar.Exposure.Token[0] == 'a'
                ? "b" + similar.Exposure.Token[1..]
                : "a" + similar.Exposure.Token[1..]
        };

        var eventId = Guid.NewGuid();
        HttpResponseMessage response = await PostEventAsync(client, Payload(eventId, sourceId, peerId, exposure));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await GetStoredEvent(eventId)).Should().BeNull();
    }

    [Fact]
    public async Task IngestRecommendationEvent_WhenReplayed_Returns202AndKeepsOneRow()
    {
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        (Guid sourceId, Guid peerId) = await SimilarAssetsSeed.EnsurePublicPairAsync(scopeFactory);
        HttpClient client = fixture.Factory.CreateClient();
        SimilarAssetsResponse similar = await GetSimilar(client, sourceId);
        var eventId = Guid.NewGuid();
        var payload = Payload(eventId, sourceId, peerId, similar.Exposure!);

        (await PostEventAsync(client, payload)).StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await PostEventAsync(client, payload)).StatusCode.Should().Be(HttpStatusCode.Accepted);

        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.RecommendationEvents.AsNoTracking().CountAsync(e => e.Id == eventId)).Should().Be(1);
    }

    [Fact]
    public async Task IngestRecommendationEvent_WhenPersonalTokenReplayedAnonymously_Returns202WithoutStoring()
    {
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        (Guid sourceId, Guid peerId) = await SimilarAssetsSeed.EnsurePublicPairAsync(scopeFactory);
        (HttpClient clientA, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        SimilarAssetsExposureResponse exposure = await GetPersonalExposureAsync(clientA, sourceId, peerId);
        HttpClient anonymous = fixture.Factory.CreateClient();

        var eventId = Guid.NewGuid();
        HttpResponseMessage response = await PostEventAsync(
            anonymous,
            Payload(eventId, sourceId, peerId, exposure));

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await GetStoredEvent(eventId)).Should().BeNull();
    }

    [Fact]
    public async Task IngestRecommendationEvent_WhenPersonalTokenReplayedByForeignActor_Returns202WithoutStoringAndKeepsOwnerSlot()
    {
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        (Guid sourceId, Guid peerId) = await SimilarAssetsSeed.EnsurePublicPairAsync(scopeFactory);
        (HttpClient clientA, var usernameA) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        (HttpClient clientB, _) = await IntegrationTestAuth.RegisterAndAuthenticateAsync(fixture.Factory);
        SimilarAssetsExposureResponse exposure = await GetPersonalExposureAsync(clientA, sourceId, peerId);

        var eventId = Guid.NewGuid();
        var payload = Payload(eventId, sourceId, peerId, exposure);

        HttpResponseMessage foreign = await PostEventAsync(clientB, payload);
        foreign.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await GetStoredEvent(eventId)).Should().BeNull();

        HttpResponseMessage owner = await PostEventAsync(clientA, payload);
        owner.StatusCode.Should().Be(HttpStatusCode.Accepted);
        RecommendationEvent? stored = await GetStoredEvent(eventId);
        stored.Should().NotBeNull();
        stored.TargetAssetId.Should().Be(peerId);
        stored.ActorUserId.Should().Be(await FindUserIdAsync(usernameA));
    }

    private static async Task<SimilarAssetsResponse> GetSimilar(HttpClient client, Guid sourceId)
    {
        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/assets/{sourceId}/similar?limit=6", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        SimilarAssetsResponse? payload = await response.Content.ReadFromJsonAsync<SimilarAssetsResponse>();
        payload.Should().NotBeNull();
        return payload;
    }

    private static async Task<SimilarAssetsExposureResponse> GetPersonalExposureAsync(
        HttpClient client,
        Guid sourceId,
        Guid peerId)
    {
        HttpResponseMessage patch = await client.PatchAsJsonAsync(
            new Uri("/api/users/me/recommendation-preferences", UriKind.Relative),
            new { isPersonalized = true });
        patch.EnsureSuccessStatusCode();

        HttpResponseMessage response = await client.GetAsync(
            new Uri($"/api/users/me/assets/{sourceId}/similar?limit=6", UriKind.Relative));
        response.EnsureSuccessStatusCode();
        SimilarAssetsResponse? payload = await response.Content.ReadFromJsonAsync<SimilarAssetsResponse>();
        payload.Should().NotBeNull();
        payload.Items.Should().ContainSingle(i => i.Id == peerId);
        payload.Exposure.Should().NotBeNull();
        payload.Exposure!.RankingVersion.Should().Be("similar-assets-v1-personal");
        return payload.Exposure;
    }

    private async Task<Guid> FindUserIdAsync(string username)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
    }

    private static async Task<HttpResponseMessage> PostEventAsync(HttpClient client, object payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _eventsUri);
        request.Content = JsonContent.Create(payload);
        foreach ((var key, var value) in AnalyticsRateLimitTestHost.CreateSignedHeaders(
                     TEST_CLIENT_IP,
                     AssetBlockWebApplicationFactory.TEST_ANALYTICS_BFF_SIGNING_SECRET))
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        return await client.SendAsync(request);
    }

    private async Task<RecommendationEvent?> GetStoredEvent(Guid eventId)
    {
        await using AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope();
        ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.RecommendationEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId);
    }

    private static object Payload(
        Guid eventId,
        Guid sourceId,
        Guid targetId,
        SimilarAssetsExposureResponse exposure) =>
        new
        {
            eventId,
            eventType = "IMPRESSION",
            visitorId = Guid.NewGuid(),
            sessionId = Guid.NewGuid(),
            sourceAssetId = sourceId,
            targetAssetId = targetId,
            slotPosition = 0,
            exposureId = exposure.Id,
            rankingVersion = exposure.RankingVersion,
            expiresAt = exposure.ExpiresAt,
            exposureToken = exposure.Token,
            candidateIds = new[] { targetId },
            deviceClass = "DESKTOP"
        };

    private sealed record SimilarAssetsResponse(
        IReadOnlyList<SimilarAssetItemResponse> Items,
        SimilarAssetsExposureResponse? Exposure);

    private sealed record SimilarAssetItemResponse(Guid Id);

    private sealed record SimilarAssetsExposureResponse(
        Guid Id,
        string RankingVersion,
        DateTimeOffset ExpiresAt,
        string Token);
}
