using System.Net;
using System.Net.Http.Json;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.IntegrationTests.Support;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class PaymentsCheckoutAttributionIntegrationTests(IntegrationTestFixture fixture)
{
    private static readonly Uri _checkoutUri = new("/api/payments/checkout", UriKind.Relative);
    private static readonly Uri _bundleCheckoutUri = new("/api/payments/checkout/bundles", UriKind.Relative);

    [Fact]
    public async Task CreateCheckout_WithoutAttribution_ShouldSucceedWithNoAttributionStored()
    {
        (_, Guid assetId) = await SeedSellerAsset(price: 11m);
        (HttpClient buyerClient, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        var response = await buyerClient.PostAsJsonAsync(_checkoutUri, new CreateCheckoutRequest(assetId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var intent = await GetPendingIntentForAsset(assetId);
        intent.AttributionSource.Should().BeNull();
        intent.AttributionCollectionId.Should().BeNull();
        intent.AttributionReferrerHost.Should().BeNull();
        intent.AnalyticsVisitorId.Should().BeNull();
        intent.AnalyticsSessionId.Should().BeNull();
    }

    [Fact]
    public async Task CreateCheckout_WithVerifiedCollectionAttribution_ShouldStoreCollectionAttribution()
    {
        (Guid sellerId, Guid assetId) = await SeedSellerAsset(price: 13m);
        var collectionId = await SeedPublishedCollection(sellerId, assetId);
        (HttpClient buyerClient, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var visitorId = Guid.NewGuid();
        var sessionId = Guid.NewGuid();

        var response = await buyerClient.PostAsJsonAsync(
            _checkoutUri,
            new CreateCheckoutRequest(
                assetId,
                new CheckoutAttributionRequest(AnalyticsTrafficSource.COLLECTION, collectionId, null),
                visitorId,
                sessionId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var intent = await GetPendingIntentForAsset(assetId);
        intent.AttributionSource.Should().Be(AnalyticsTrafficSource.COLLECTION);
        intent.AttributionCollectionId.Should().Be(collectionId);
        intent.AttributionReferrerHost.Should().BeNull();
        intent.AnalyticsVisitorId.Should().Be(visitorId);
        intent.AnalyticsSessionId.Should().Be(sessionId);
    }

    [Fact]
    public async Task CreateCheckout_WithExternalAttribution_ShouldStoreBareReferrerHost()
    {
        (_, Guid assetId) = await SeedSellerAsset(price: 14m);
        (HttpClient buyerClient, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        var response = await buyerClient.PostAsJsonAsync(
            _checkoutUri,
            new CreateCheckoutRequest(
                assetId,
                new CheckoutAttributionRequest(
                    AnalyticsTrafficSource.EXTERNAL,
                    null,
                    "https://Partner.Example.com/deals?ref=abc")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var intent = await GetPendingIntentForAsset(assetId);
        intent.AttributionSource.Should().Be(AnalyticsTrafficSource.EXTERNAL);
        intent.AttributionReferrerHost.Should().Be("partner.example.com");
    }

    [Fact]
    public async Task CreateCheckout_WhenCollectionBelongsToAnotherSeller_ShouldDropAttribution()
    {
        (_, Guid assetId) = await SeedSellerAsset(price: 15m);
        (Guid otherSellerId, Guid otherAssetId) = await SeedSellerAsset(price: 16m);
        var foreignCollectionId = await SeedPublishedCollection(otherSellerId, otherAssetId);
        (HttpClient buyerClient, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        var response = await buyerClient.PostAsJsonAsync(
            _checkoutUri,
            new CreateCheckoutRequest(
                assetId,
                new CheckoutAttributionRequest(AnalyticsTrafficSource.COLLECTION, foreignCollectionId, null)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var intent = await GetPendingIntentForAsset(assetId);
        intent.AttributionSource.Should().BeNull();
        intent.AttributionCollectionId.Should().BeNull();
    }

    [Fact]
    public async Task CreateCheckout_WhenResumingPendingIntent_ShouldKeepOriginalAttribution()
    {
        (_, Guid assetId) = await SeedSellerAsset(price: 17m);
        (HttpClient buyerClient, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        var first = await buyerClient.PostAsJsonAsync(
            _checkoutUri,
            new CreateCheckoutRequest(
                assetId,
                new CheckoutAttributionRequest(AnalyticsTrafficSource.EXTERNAL, null, "first.example.com")));
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var created = await GetPendingIntentForAsset(assetId);

        var resumed = await buyerClient.PostAsJsonAsync(
            _checkoutUri,
            new CreateCheckoutRequest(
                assetId,
                new CheckoutAttributionRequest(AnalyticsTrafficSource.SEARCH, null, null)));

        resumed.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterResume = await GetPendingIntentForAsset(assetId);
        afterResume.Id.Should().Be(created.Id);
        afterResume.AttributionSource.Should().Be(AnalyticsTrafficSource.EXTERNAL);
        afterResume.AttributionReferrerHost.Should().Be("first.example.com");
    }

    [Fact]
    public async Task CreateBundleCheckout_WithCollectionAttribution_ShouldDropAttribution()
    {
        (Guid sellerId, Guid assetA) = await SeedSellerAsset(price: 10m);
        (_, Guid assetB) = await SeedSellerAsset(price: 20m, sellerId);
        var collectionId = await SeedPublishedCollection(sellerId, assetA);
        var bundleId = await SeedBundle(sellerId, assetA, assetB);
        (HttpClient buyerClient, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        var response = await buyerClient.PostAsJsonAsync(
            _bundleCheckoutUri,
            new CreateBundleCheckoutRequest(
                bundleId,
                new CheckoutAttributionRequest(AnalyticsTrafficSource.COLLECTION, collectionId, null)));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var intent = await db.CheckoutIntents.AsNoTracking()
            .SingleAsync(i => i.BundleId == bundleId && i.Status == CheckoutIntentStatus.PENDING);
        intent.AttributionSource.Should().BeNull();
        intent.AttributionCollectionId.Should().BeNull();
    }

    [Fact]
    public async Task CreateBundleCheckout_WithExternalAttribution_ShouldStoreBareReferrerHost()
    {
        (Guid sellerId, Guid assetA) = await SeedSellerAsset(price: 10m);
        (_, Guid assetB) = await SeedSellerAsset(price: 30m, sellerId);
        var bundleId = await SeedBundle(sellerId, assetA, assetB);
        (HttpClient buyerClient, _) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        var response = await buyerClient.PostAsJsonAsync(
            _bundleCheckoutUri,
            new CreateBundleCheckoutRequest(
                bundleId,
                new CheckoutAttributionRequest(
                    AnalyticsTrafficSource.EXTERNAL,
                    null,
                    "https://Deals.Example.net/bundle")));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var intent = await db.CheckoutIntents.AsNoTracking()
            .SingleAsync(i => i.BundleId == bundleId && i.Status == CheckoutIntentStatus.PENDING);
        intent.AttributionSource.Should().Be(AnalyticsTrafficSource.EXTERNAL);
        intent.AttributionReferrerHost.Should().Be("deals.example.net");
    }

    private async Task<(Guid SellerId, Guid AssetId)> SeedSellerAsset(decimal price, Guid? existingSellerId = null)
    {
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        Guid sellerId;
        if (existingSellerId is { } known)
        {
            sellerId = known;
        }
        else
        {
            (_, string sellerUsername) = await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
            sellerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, sellerUsername);
        }

        var (assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: price);
        return (sellerId, assetId);
    }

    private async Task<Guid> SeedPublishedCollection(Guid sellerId, Guid assetId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var now = DateTimeOffset.UtcNow;
        var collectionId = Guid.NewGuid();
        db.Collections.Add(new Collection
        {
            Id = collectionId,
            SellerId = sellerId,
            Title = $"Attributed collection {collectionId:N}",
            Status = CollectionStatus.PUBLISHED,
            PublishedAt = now,
            CreatedAt = now
        });
        db.CollectionItems.Add(new CollectionItem
        {
            CollectionId = collectionId,
            AssetId = assetId,
            Position = 1,
            CreatedAt = now
        });
        await db.SaveChangesAsync();
        return collectionId;
    }

    private async Task<Guid> SeedBundle(Guid sellerId, Guid assetA, Guid assetB)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var assetRows = await db.Assets.AsNoTracking()
            .Where(a => a.Id == assetA || a.Id == assetB)
            .ToDictionaryAsync(a => a.Id);
        var now = DateTimeOffset.UtcNow;
        var bundleId = Guid.NewGuid();
        var revisionId = Guid.NewGuid();
        var listPriceTotal = assetRows[assetA].Price + assetRows[assetB].Price;

        db.Bundles.Add(new Bundle
        {
            Id = bundleId,
            SellerId = sellerId,
            CreatedAt = now
        });
        db.BundleRevisions.Add(new BundleRevision
        {
            Id = revisionId,
            BundleId = bundleId,
            RevisionNumber = 1,
            IsCurrent = true,
            Title = $"Attributed bundle {bundleId:N}",
            Price = listPriceTotal - 1m,
            Currency = "usd",
            ListPriceTotal = listPriceTotal,
            CreatedAt = now
        });
        db.BundleRevisionItems.AddRange(
            new BundleRevisionItem
            {
                Id = Guid.NewGuid(),
                BundleRevisionId = revisionId,
                AssetId = assetA,
                Position = 1,
                AssetTitleSnapshot = assetRows[assetA].Title,
                ListPriceSnapshot = assetRows[assetA].Price
            },
            new BundleRevisionItem
            {
                Id = Guid.NewGuid(),
                BundleRevisionId = revisionId,
                AssetId = assetB,
                Position = 2,
                AssetTitleSnapshot = assetRows[assetB].Title,
                ListPriceSnapshot = assetRows[assetB].Price
            });
        await db.SaveChangesAsync();
        return bundleId;
    }

    private async Task<CheckoutIntent> GetPendingIntentForAsset(Guid assetId)
    {
        await using var scope = fixture.Factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        return await db.CheckoutIntents.AsNoTracking()
            .SingleAsync(i => i.AssetId == assetId && i.Status == CheckoutIntentStatus.PENDING);
    }
}
