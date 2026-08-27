using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class PurchaseStorePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task ListForUser_WhenPurchasesExist_ShouldProjectLibraryFieldsAndReviewState()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(buyer);
        await db.SaveChangesAsync();

        var reviewedAsset = TestData.CreateAsset(author.Id, category.Id, title: "Reviewed Pack", price: 12.50m);
        var plainAsset = TestData.CreateAsset(author.Id, category.Id, title: "Plain Pack", price: 3.00m);
        db.Assets.AddRange(reviewedAsset, plainAsset);
        var reviewedVersion = TestData.CreateAssetVersion(reviewedAsset.Id);
        var plainVersion = TestData.CreateAssetVersion(plainAsset.Id);
        db.AssetVersions.AddRange(reviewedVersion, plainVersion);
        await db.SaveChangesAsync();

        var older = DateTimeOffset.UtcNow.AddDays(-2);
        var newer = DateTimeOffset.UtcNow.AddDays(-1);
        TestData.AddCompletedPurchase(
            db,
            TestData.CreatePurchase(buyer.Id, reviewedAsset.Id, reviewedVersion.Id, purchasedAt: older),
            reviewedAsset.Title,
            author.Id,
            pricePaid: 12.50m);
        TestData.AddCompletedPurchase(
            db,
            TestData.CreatePurchase(buyer.Id, plainAsset.Id, plainVersion.Id, purchasedAt: newer),
            plainAsset.Title,
            author.Id,
            pricePaid: 3.00m);
        db.Reviews.Add(TestData.CreateReview(buyer.Id, reviewedAsset.Id, rating: 4));
        await db.SaveChangesAsync();

        var store = new PurchaseStore(db);
        var result = await store.ListForUser(buyer.Id, new ListMyPurchasesRequest
        {
            Page = 1,
            PageSize = 10,
            SortBy = "PurchasedAt",
            SortDirection = SortDirection.DESC
        });

        result.TotalCount.Should().Be(2);
        result.Items.Should().HaveCount(2);
        result.Items[0].AssetTitle.Should().Be("Plain Pack");
        result.Items[0].Price.Should().Be(3.00m);
        result.Items[0].AuthorUsername.Should().Be(author.Username);
        result.Items[0].HasUserReviewed.Should().BeFalse();
        result.Items[1].AssetTitle.Should().Be("Reviewed Pack");
        result.Items[1].Price.Should().Be(12.50m);
        result.Items[1].AuthorUsername.Should().Be(author.Username);
        result.Items[1].HasUserReviewed.Should().BeTrue();
    }

    [Fact]
    public async Task ListForUser_WhenPagingByPurchasedAt_ShouldReturnStablePages()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("buyer2", "buyer2@example.test");
        db.Users.Add(buyer);
        await db.SaveChangesAsync();

        var baseTime = DateTimeOffset.UtcNow.AddHours(-10);
        for (var i = 0; i < 5; i++)
        {
            var asset = TestData.CreateAsset(author.Id, category.Id, title: $"Asset {i}");
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
            var version = TestData.CreateAssetVersion(asset.Id);
            db.AssetVersions.Add(version);
            await db.SaveChangesAsync();
            TestData.AddCompletedPurchase(
                db,
                TestData.CreatePurchase(buyer.Id, asset.Id, version.Id, purchasedAt: baseTime.AddMinutes(i)),
                asset.Title,
                author.Id);
            await db.SaveChangesAsync();
        }

        var store = new PurchaseStore(db);
        var page1 = await store.ListForUser(buyer.Id, new ListMyPurchasesRequest
        {
            Page = 1,
            PageSize = 2,
            SortBy = "PurchasedAt",
            SortDirection = SortDirection.ASC
        });
        var page2 = await store.ListForUser(buyer.Id, new ListMyPurchasesRequest
        {
            Page = 2,
            PageSize = 2,
            SortBy = "PurchasedAt",
            SortDirection = SortDirection.ASC
        });

        page1.TotalCount.Should().Be(5);
        page1.Items.Select(i => i.AssetTitle).Should().Equal("Asset 0", "Asset 1");
        page2.Items.Select(i => i.AssetTitle).Should().Equal("Asset 2", "Asset 3");
    }

    [Fact]
    public async Task ListForUser_WhenPurchasedAtTies_ShouldOrderByIdAsTieBreaker()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("buyer3", "buyer3@example.test");
        db.Users.Add(buyer);
        await db.SaveChangesAsync();

        var sharedTime = DateTimeOffset.UtcNow.AddHours(-1);
        var idLow = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var idHigh = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "A");
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "B");
        db.Assets.AddRange(assetA, assetB);
        var versionA = TestData.CreateAssetVersion(assetA.Id);
        var versionB = TestData.CreateAssetVersion(assetB.Id);
        db.AssetVersions.AddRange(versionA, versionB);
        await db.SaveChangesAsync();

        TestData.AddCompletedPurchase(
            db,
            TestData.CreatePurchase(buyer.Id, assetA.Id, versionA.Id, purchasedAt: sharedTime, id: idHigh),
            assetA.Title,
            author.Id);
        TestData.AddCompletedPurchase(
            db,
            TestData.CreatePurchase(buyer.Id, assetB.Id, versionB.Id, purchasedAt: sharedTime, id: idLow),
            assetB.Title,
            author.Id);
        await db.SaveChangesAsync();

        var store = new PurchaseStore(db);
        var page = await store.ListForUser(buyer.Id, new ListMyPurchasesRequest
        {
            Page = 1,
            PageSize = 10,
            SortBy = "PurchasedAt",
            SortDirection = SortDirection.ASC
        });

        page.Items.Select(i => i.Id).Should().Equal(idLow, idHigh);
    }

    [Fact]
    public async Task Add_WhenOrderLineIdDuplicates_ShouldThrowDuplicateEntitlementException()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyerA = TestData.CreateUser("dup-line-a", "dup-line-a@example.test");
        var buyerB = TestData.CreateUser("dup-line-b", "dup-line-b@example.test");
        db.Users.AddRange(buyerA, buyerB);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "Line Dup A");
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "Line Dup B");
        db.Assets.AddRange(assetA, assetB);
        var versionA = TestData.CreateAssetVersion(assetA.Id);
        var versionB = TestData.CreateAssetVersion(assetB.Id);
        db.AssetVersions.AddRange(versionA, versionB);
        await db.SaveChangesAsync();

        var first = TestData.CreatePurchase(buyerA.Id, assetA.Id, versionA.Id);
        TestData.AddCompletedPurchase(db, first, assetA.Title, author.Id);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var duplicate = TestData.CreatePurchase(buyerB.Id, assetB.Id, versionB.Id, orderLineId: first.OrderLineId);
        var act = () => new PurchaseStore(db).Add(duplicate);

        await act.Should().ThrowAsync<DuplicateEntitlementException>();
    }

    [Fact]
    public async Task Add_WhenUserIdAssetIdDuplicatesWithNewOrderLine_ShouldThrowDuplicateEntitlementException()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("dup-user-asset", "dup-user-asset@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "User Asset Dup");
        db.Assets.Add(asset);
        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var first = TestData.CreatePurchase(buyer.Id, asset.Id, version.Id);
        TestData.AddCompletedPurchase(db, first, asset.Title, author.Id, stripeSessionId: "cs_user_asset_first");
        await db.SaveChangesAsync();

        var conflict = TestData.CreatePurchase(buyer.Id, asset.Id, version.Id);
        TestData.AddCompletedCheckoutIntent(
            db,
            conflict,
            asset.Title,
            author.Id,
            stripeSessionId: "cs_user_asset_second");
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new PurchaseStore(db);
        var act = () => store.Add(conflict);

        await act.Should().ThrowAsync<DuplicateEntitlementException>();
    }

    [Fact]
    public async Task ListForUser_WhenAssetIsSoftDeleted_ShouldStillReturnPurchaseAndPreserveLibraryEntitlement()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("library-buyer", "library-buyer@example.test");
        db.Users.Add(buyer);
        await db.SaveChangesAsync();

        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Soft Deleted Pack", price: 19.99m);
        db.Assets.Add(asset);
        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        TestData.AddCompletedPurchase(
            db,
            TestData.CreatePurchase(buyer.Id, asset.Id, version.Id),
            asset.Title,
            author.Id,
            pricePaid: 19.99m);
        await db.SaveChangesAsync();

        // Soft-delete the asset after purchase
        var assetStore = new AssetStore(db);
        await assetStore.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        // Verify public lookup returns null due to global query filter
        var publicLookup = await assetStore.GetById(asset.Id);
        publicLookup.Should().BeNull();

        // Verify purchaser library query still includes the soft-deleted asset with matching total count
        var store = new PurchaseStore(db);
        var result = await store.ListForUser(buyer.Id, new ListMyPurchasesRequest
        {
            Page = 1,
            PageSize = 10,
            SortBy = "PurchasedAt",
            SortDirection = SortDirection.DESC
        });

        result.TotalCount.Should().Be(1);
        result.Items.Should().HaveCount(1);
        result.Items[0].AssetId.Should().Be(asset.Id);
        result.Items[0].AssetTitle.Should().Be("Soft Deleted Pack");
        result.Items[0].Price.Should().Be(19.99m);
        result.Items[0].AuthorUsername.Should().Be(author.Username);
    }
}

