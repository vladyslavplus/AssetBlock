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
        (HttpClient _, var sellerUsername) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        Guid sellerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, sellerUsername);
        (Guid assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId);

        var collectionId = Guid.NewGuid();
        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
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

        HttpClient anonymous = fixture.Factory.CreateClient();
        HttpResponseMessage response = await anonymous.GetAsync(new Uri($"/api/collections/{collectionId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetBundle_WhenArchived_ShouldReturn404()
    {
        (HttpClient _, var sellerUsername) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        Guid sellerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, sellerUsername);
        (Guid assetA, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 10m);
        (Guid assetB, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 20m);

        Guid bundleId;
        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Dictionary<Guid, Asset> assetRows = await db.Assets.AsNoTracking()
                .Where(a => a.Id == assetA || a.Id == assetB)
                .ToDictionaryAsync(a => a.Id);
            DateTimeOffset now = DateTimeOffset.UtcNow;
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

        HttpClient anonymous = fixture.Factory.CreateClient();
        HttpResponseMessage response = await anonymous.GetAsync(new Uri($"/api/bundles/{bundleId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetBundle_WhenAvailable_ShouldSerializeLicenseCodeAsStringOrNull()
    {
        (HttpClient _, var sellerUsername) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        Guid sellerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, sellerUsername);
        (Guid assetA, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 10m);
        (Guid assetB, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 20m);

        Guid bundleId;
        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Dictionary<Guid, Asset> assetRows = await db.Assets.AsNoTracking()
                .Where(a => a.Id == assetA || a.Id == assetB)
                .ToDictionaryAsync(a => a.Id);
            DateTimeOffset now = DateTimeOffset.UtcNow;
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

        HttpClient anonymous = fixture.Factory.CreateClient();
        HttpResponseMessage response = await anonymous.GetAsync(new Uri($"/api/bundles/{bundleId}", UriKind.Relative));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        JsonElement items = doc.RootElement.GetProperty("items");
        items.GetArrayLength().Should().Be(2);
        foreach (JsonElement item in items.EnumerateArray())
        {
            JsonElement license = item.GetProperty("licenseCode");
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
        (HttpClient _, var sellerUsername) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        Guid sellerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, sellerUsername);
        (Guid assetId, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 12.50m);

        (HttpClient buyerClient, _) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);

        HttpResponseMessage response = await buyerClient.PostAsJsonAsync(
            new Uri("/api/payments/checkout", UriKind.Relative),
            new CreateCheckoutRequest(assetId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        CreateCheckoutSessionResponse? body = await response.Content.ReadFromJsonAsync<CreateCheckoutSessionResponse>(
            IntegrationTestAuth.JsonOptions);
        body.Should().NotBeNull();
        body.CheckoutUrl.Should().StartWith("https://checkout.test/");
    }

    [Fact]
    public async Task CreateBundleCheckout_WhenAssetAlreadyReserved_ShouldReturnConflictCode()
    {
        (HttpClient _, var sellerUsername) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        Guid sellerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, sellerUsername);
        (Guid assetA, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 10m);
        (Guid assetB, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 20m);

        Guid bundleId;
        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Dictionary<Guid, Asset> assetRows = await db.Assets.AsNoTracking()
                .Where(a => a.Id == assetA || a.Id == assetB)
                .ToDictionaryAsync(a => a.Id);
            DateTimeOffset now = DateTimeOffset.UtcNow;
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

        HttpResponseMessage assetCheckout = await buyerClient.PostAsJsonAsync(
            new Uri("/api/payments/checkout", UriKind.Relative),
            new CreateCheckoutRequest(assetA));
        assetCheckout.StatusCode.Should().Be(HttpStatusCode.OK);

        HttpResponseMessage bundleCheckout = await buyerClient.PostAsJsonAsync(
            new Uri("/api/payments/checkout/bundles", UriKind.Relative),
            new CreateBundleCheckoutRequest(bundleId));

        bundleCheckout.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await bundleCheckout.Content.ReadAsStringAsync();
        body.Should().Contain("ERR_CHECKOUT_ITEM_RESERVED");
    }

    [Fact]
    public async Task CreateBundleCheckout_WhenAvailable_ShouldPersistExactCentAllocations()
    {
        (HttpClient _, var sellerUsername) =
            await IntegrationTestAuth.RegisterVerifiedAndAuthenticateAsync(fixture.Factory);
        IServiceScopeFactory scopeFactory = fixture.Factory.Services.GetRequiredService<IServiceScopeFactory>();
        Guid sellerId = await AssetVersionsSeed.GetUserIdAsync(scopeFactory, sellerUsername);
        (Guid assetA, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 10.00m);
        (Guid assetB, _) = await AssetVersionsSeed.SeedAssetWithVersionsAsync(scopeFactory, sellerId, price: 30.00m);

        Guid bundleId;
        const decimal bundlePrice = 25.00m;
        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            Dictionary<Guid, Asset> assetRows = await db.Assets.AsNoTracking()
                .Where(a => a.Id == assetA || a.Id == assetB)
                .ToDictionaryAsync(a => a.Id);
            DateTimeOffset now = DateTimeOffset.UtcNow;
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
                Title = "Discounted Bundle",
                Price = bundlePrice,
                Currency = "usd",
                ListPriceTotal = 40.00m,
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

        HttpResponseMessage response = await buyerClient.PostAsJsonAsync(
            new Uri("/api/payments/checkout/bundles", UriKind.Relative),
            new CreateBundleCheckoutRequest(bundleId));

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        CreateCheckoutSessionResponse? result = await response.Content.ReadFromJsonAsync<CreateCheckoutSessionResponse>(
            IntegrationTestAuth.JsonOptions);
        result.Should().NotBeNull();
        result.CheckoutUrl.Should().StartWith("https://checkout.test/");

        await using (AsyncServiceScope scope = fixture.Factory.Services.CreateAsyncScope())
        {
            ApplicationDbContext db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            CheckoutIntent? intent = await db.CheckoutIntents
                .Include(ci => ci.Items)
                .SingleOrDefaultAsync(ci => ci.BundleId == bundleId);

            intent.Should().NotBeNull();
            intent.AmountTotal.Should().Be(bundlePrice);
            intent.Items.Should().HaveCount(2);
            intent.Items.Sum(i => i.AllocatedPrice).Should().Be(bundlePrice);
            intent.Items.Should().OnlyContain(i => i.AllocatedPrice >= 0.01m);
            intent.Items.Should().OnlyContain(i => decimal.Round(i.AllocatedPrice, 2, MidpointRounding.AwayFromZero) == i.AllocatedPrice);
        }
    }
}
