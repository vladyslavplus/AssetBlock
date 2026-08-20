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
public sealed class AnalyticsEventsControllerIntegrationTests(IntegrationTestFixture fixture)
{
    private static readonly Uri _eventsUri = new("/api/analytics/events", UriKind.Relative);
    private const string TEST_CLIENT_IP = "198.51.100.42";

    [Fact]
    public async Task IngestEvent_WhenTargetDoesNotMatchEventType_Returns400()
    {
        var client = fixture.Factory.CreateClient();

        var response = await PostEventAsync(client, Payload(assetId: null));

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await response.Content.ReadAsStringAsync()).Should().Contain(ErrorCodes.ERR_ANALYTICS_EVENT_INVALID);
    }

    [Fact]
    public async Task IngestEvent_WhenVisitorIdIsMissing_Returns400()
    {
        var client = fixture.Factory.CreateClient();
        var payload = Payload(assetId: Guid.NewGuid()) with { VisitorId = Guid.Empty };

        var response = await PostEventAsync(client, payload);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task IngestEvent_WhenAnonymousAndAssetIsPublic_Returns202AndStoresEvent()
    {
        var client = fixture.Factory.CreateClient();
        var assetId = await AssetCatalogSeed.EnsureSampleAssetAsync(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>());
        var payload = Payload(assetId);

        var response = await PostEventAsync(client, payload);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();

        var stored = await GetStoredEvent(payload.EventId);
        stored.Should().NotBeNull();
        stored.AssetId.Should().Be(assetId);
        stored.ActorUserId.Should().BeNull();
    }

    [Fact]
    public async Task IngestEvent_WhenAssetDoesNotExist_Returns202WithoutStoringEvent()
    {
        var client = fixture.Factory.CreateClient();
        var payload = Payload(assetId: Guid.NewGuid());

        var response = await PostEventAsync(client, payload);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await GetStoredEvent(payload.EventId)).Should().BeNull();
    }

    [Fact]
    public async Task IngestEvent_WhenSellerViewsOwnAsset_Returns202WithoutStoringEvent()
    {
        var (client, username) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var assetId = await SeedAssetForAuthor(username);
        var payload = Payload(assetId);

        var response = await PostEventAsync(client, payload);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await GetStoredEvent(payload.EventId)).Should().BeNull();
    }

    [Fact]
    public async Task IngestEvent_WhenAuthenticatedVisitorIsNotTheSeller_Returns202AndRecordsActor()
    {
        var (client, username) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var assetId = await AssetCatalogSeed.EnsureSampleAssetAsync(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>());
        var payload = Payload(assetId);

        var response = await PostEventAsync(client, payload);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var stored = await GetStoredEvent(payload.EventId);
        stored.Should().NotBeNull();
        stored.ActorUserId.Should().Be(await GetUserId(username));
    }

    [Fact]
    public async Task IngestEvent_WhenReplayed_Returns202AndKeepsOneRow()
    {
        var client = fixture.Factory.CreateClient();
        var assetId = await AssetCatalogSeed.EnsureSampleAssetAsync(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>());
        var payload = Payload(assetId);

        var first = await PostEventAsync(client, payload);
        var replay = await PostEventAsync(client, payload);

        first.StatusCode.Should().Be(HttpStatusCode.Accepted);
        replay.StatusCode.Should().Be(HttpStatusCode.Accepted);

        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        (await db.AnalyticsEvents.AsNoTracking().CountAsync(e => e.Id == payload.EventId)).Should().Be(1);
    }

    [Fact]
    public async Task IngestEvent_WhenDownloadIsNotEntitled_Returns202WithoutStoringEvent()
    {
        var (client, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var assetId = await AssetCatalogSeed.EnsureSampleAssetAsync(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>());
        var versionId = await GetCurrentVersionId(assetId);
        var payload = Payload(assetId) with
        {
            EventType = nameof(AnalyticsEventType.DOWNLOAD_REQUESTED),
            AssetVersionId = versionId
        };

        var response = await PostEventAsync(client, payload);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await GetStoredEvent(payload.EventId)).Should().BeNull();
    }

    [Fact]
    public async Task IngestEvent_WhenExternalReferrerIsAUrl_ShouldStoreOnlyTheHost()
    {
        var client = fixture.Factory.CreateClient();
        var assetId = await AssetCatalogSeed.EnsureSampleAssetAsync(
            fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>());
        var payload = Payload(assetId) with
        {
            Source = nameof(AnalyticsTrafficSource.EXTERNAL),
            ReferrerHost = "https://Referring.Example.com/landing?utm_campaign=secret"
        };

        var response = await PostEventAsync(client, payload);

        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        (await GetStoredEvent(payload.EventId))!.ReferrerHost.Should().Be("referring.example.com");
    }

    private static async Task<HttpResponseMessage> PostEventAsync(HttpClient client, AnalyticsEventPayload payload)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, _eventsUri);
        request.Content = JsonContent.Create(payload);

        foreach (var (key, value) in AnalyticsRateLimitTestHost.CreateSignedHeaders(
                     TEST_CLIENT_IP,
                     AssetBlockWebApplicationFactory.TEST_ANALYTICS_BFF_SIGNING_SECRET))
        {
            request.Headers.TryAddWithoutValidation(key, value);
        }

        return await client.SendAsync(request);
    }

    private async Task<AnalyticsEvent?> GetStoredEvent(Guid eventId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.AnalyticsEvents.AsNoTracking().SingleOrDefaultAsync(e => e.Id == eventId);
    }

    private async Task<Guid> GetUserId(string username)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.Users.AsNoTracking().Where(u => u.Username == username).Select(u => u.Id).SingleAsync();
    }

    private async Task<Guid> GetCurrentVersionId(Guid assetId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.AssetVersions.AsNoTracking()
            .Where(v => v.AssetId == assetId && v.IsCurrent)
            .Select(v => v.Id)
            .SingleAsync();
    }

    private async Task<Guid> SeedAssetForAuthor(string username)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var authorId = await db.Users.AsNoTracking()
            .Where(u => u.Username == username)
            .Select(u => u.Id)
            .SingleAsync();
        var categoryId = await db.Categories.AsNoTracking().Select(c => c.Id).FirstAsync();

        var asset = new Asset
        {
            Id = Guid.NewGuid(),
            AuthorId = authorId,
            CategoryId = categoryId,
            Title = $"Self view asset {Guid.NewGuid():N}",
            Price = 4.99m,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return asset.Id;
    }

    private static AnalyticsEventPayload Payload(Guid? assetId) =>
        new(
            Guid.NewGuid(),
            nameof(AnalyticsEventType.ASSET_VIEW),
            Guid.NewGuid(),
            Guid.NewGuid(),
            assetId,
            AssetVersionId: null,
            BundleId: null,
            CollectionId: null,
            nameof(AnalyticsTrafficSource.CATALOG),
            ReferrerHost: null,
            nameof(AnalyticsDeviceClass.DESKTOP));

    /// <summary>Loosely typed payload so malformed envelopes can be posted over the real HTTP pipeline.</summary>
    private sealed record AnalyticsEventPayload(
        Guid EventId,
        string EventType,
        Guid VisitorId,
        Guid SessionId,
        Guid? AssetId,
        Guid? AssetVersionId,
        Guid? BundleId,
        Guid? CollectionId,
        string Source,
        string? ReferrerHost,
        string DeviceClass);
}
