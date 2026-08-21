using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AssetBlock.Application.UseCases.Payments.CreateCheckoutSession;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.WebApi.IntegrationTests.Support;
using AwesomeAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace AssetBlock.WebApi.IntegrationTests.Controllers;

[Collection(nameof(IntegrationTestCollection))]
public sealed class CollectionsBundlesCheckoutIntegrationTests(IntegrationTestFixture fixture)
{
    [Fact]
    public async Task GetCollection_WhenDraft_ShouldReturn404()
    {
        (_, string sellerUsername) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var sellerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, sellerUsername);
        (Guid assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId);

        var collectionId = Guid.NewGuid();
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            db.Collections.Add(new Collection
            {
                Id = collectionId,
                SellerId = sellerId,
                Title = "Draft only",
                Status = CollectionStatus.DRAFT,
                CreatedAt = DateTimeOffset.UtcNow
            });
            db.CollectionItems.Add(new CollectionItem
            {
                CollectionId = collectionId,
                AssetId = assetId,
                Position = 1,
                CreatedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync();
        }

        var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync(new Uri($"/api/collections/{collectionId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetBundle_WhenArchived_ShouldReturn404()
    {
        (_, string sellerUsername) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var sellerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, sellerUsername);
        var (assetA, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 10m);
        var (assetB, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 20m);

        Guid bundleId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var assetRows = await db.Assets.AsNoTracking()
                .Where(a => a.Id == assetA || a.Id == assetB)
                .ToDictionaryAsync(a => a.Id);
            var now = DateTimeOffset.UtcNow;
            bundleId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
            db.Bundles.Add(new Bundle
            {
                Id = bundleId,
                SellerId = sellerId,
                CreatedAt = now,
                ArchivedAt = now
            });
            db.BundleRevisions.Add(new BundleRevision
            {
                Id = revisionId,
                BundleId = bundleId,
                RevisionNumber = 1,
                IsCurrent = true,
                Title = "Archived Bundle",
                Price = 15m,
                Currency = "usd",
                ListPriceTotal = 30m,
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
        }

        var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync(new Uri($"/api/bundles/{bundleId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetBundle_WhenAvailable_ShouldSerializeLicenseCodeAsStringOrNull()
    {
        (_, string sellerUsername) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var sellerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, sellerUsername);
        var (assetA, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 10m);
        var (assetB, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 20m);

        Guid bundleId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var assetRows = await db.Assets.AsNoTracking()
                .Where(a => a.Id == assetA || a.Id == assetB)
                .ToDictionaryAsync(a => a.Id);
            var now = DateTimeOffset.UtcNow;
            bundleId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
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
                Title = "Public Bundle",
                Price = 25m,
                Currency = "usd",
                ListPriceTotal = 30m,
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
        }

        var anonymous = fixture.Factory.CreateClient();
        var response = await anonymous.GetAsync(new Uri($"/api/bundles/{bundleId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(2);
        foreach (var item in items.EnumerateArray())
        {
            var license = item.GetProperty("licenseCode");
            license.ValueKind.Should().BeOneOf(JsonValueKind.String, JsonValueKind.Null);
            if (license.ValueKind == JsonValueKind.String)
            {
                license.GetString().Should().BeOneOf("PERSONAL", "COMMERCIAL");
            }
        }
    }

    [Fact]
    public async Task CreateCheckout_WithFakeStripe_WhenAssetAvailable_ShouldReturnCheckoutUrl()
    {
        (_, string sellerUsername) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var sellerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, sellerUsername);
        var (assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 12.50m);

        (HttpClient buyerClient, _) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        var response = await buyerClient.PostAsJsonAsync(
            new Uri("/api/payments/checkout", UriKind.Relative),
            new CreateCheckoutRequest(assetId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = await response.Content.ReadFromJsonAsync<CreateCheckoutSessionResponse>(
            IntegrationTestAuth.JsonOptions);
        body.Should().NotBeNull();
        body.CheckoutUrl.Should().StartWith("https://checkout.test/");
    }

    [Fact]
    public async Task CreateBundleCheckout_WhenAssetAlreadyReserved_ShouldReturnConflictCode()
    {
        (_, string sellerUsername) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        var scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        var sellerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, sellerUsername);
        var (assetA, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 10m);
        var (assetB, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 20m);

        Guid bundleId;
        await using (var scope = fixture.Factory.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            var assetRows = await db.Assets.AsNoTracking()
                .Where(a => a.Id == assetA || a.Id == assetB)
                .ToDictionaryAsync(a => a.Id);
            var now = DateTimeOffset.UtcNow;
            bundleId = Guid.NewGuid();
            var revisionId = Guid.NewGuid();
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
                Title = "Conflict Bundle",
                Price = 15m,
                Currency = "usd",
                ListPriceTotal = 30m,
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
        }

        (HttpClient buyerClient, _) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        var assetCheckout = await buyerClient.PostAsJsonAsync(
            new Uri("/api/payments/checkout", UriKind.Relative),
            new CreateCheckoutRequest(assetA));
        assetCheckout.StatusCode.Should().Be(HttpStatusCode.OK);

        var bundleCheckout = await buyerClient.PostAsJsonAsync(
            new Uri("/api/payments/checkout/bundles", UriKind.Relative),
            new CreateBundleCheckoutRequest(bundleId));

        bundleCheckout.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await bundleCheckout.Content.ReadAsStringAsync();
        body.Should().Contain("ERR_CHECKOUT_ITEM_RESERVED");
    }
}
