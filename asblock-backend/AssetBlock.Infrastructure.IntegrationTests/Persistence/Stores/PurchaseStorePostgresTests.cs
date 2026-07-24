using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Npgsql;

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
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, reviewedAsset.Id, reviewedVersion.Id, purchasedAt: older), reviewedAsset.Title);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, plainAsset.Id, plainVersion.Id, purchasedAt: newer), plainAsset.Title);
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
            TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, version.Id, purchasedAt: baseTime.AddMinutes(i)), asset.Title);
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

        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, assetA.Id, versionA.Id, purchasedAt: sharedTime, id: idHigh), assetA.Title);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, assetB.Id, versionB.Id, purchasedAt: sharedTime, id: idLow), assetB.Title);
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
    public async Task Add_WhenStripePaymentIdDuplicates_ShouldThrowDuplicatePurchaseException()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyerA = TestData.CreateUser("dup-stripe-a", "dup-stripe-a@example.test");
        var buyerB = TestData.CreateUser("dup-stripe-b", "dup-stripe-b@example.test");
        db.Users.AddRange(buyerA, buyerB);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "Stripe Dup A");
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "Stripe Dup B");
        db.Assets.AddRange(assetA, assetB);
        var versionA = TestData.CreateAssetVersion(assetA.Id);
        var versionB = TestData.CreateAssetVersion(assetB.Id);
        db.AssetVersions.AddRange(versionA, versionB);
        await db.SaveChangesAsync();

        const string sharedSession = "cs_dup_stripe_payment";
        var first = TestData.CreatePurchase(buyerA.Id, assetA.Id, versionA.Id, stripePaymentId: sharedSession);
        TestData.AddCompletedPurchase(db, first, assetA.Title);
        await db.SaveChangesAsync();

        var duplicate = TestData.CreatePurchase(buyerB.Id, assetB.Id, versionB.Id, stripePaymentId: sharedSession);
        // Intent must not reuse the same Stripe session (unique on checkout_intents); only the purchase row collides.
        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = duplicate.CheckoutIntentId,
            UserId = duplicate.UserId,
            AssetId = duplicate.AssetId,
            AssetVersionId = duplicate.AssetVersionId,
            AssetTitle = assetB.Title,
            UnitAmount = duplicate.PricePaid,
            Currency = duplicate.Currency,
            StripeSessionId = null,
            Status = CheckoutIntentStatus.COMPLETED,
            CreatedAt = duplicate.PurchasedAt,
            ExpiresAt = duplicate.PurchasedAt.AddHours(1),
            CompletedAt = duplicate.PurchasedAt
        });
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new PurchaseStore(db);
        var act = () => store.Add(duplicate);

        await act.Should().ThrowAsync<DuplicatePurchaseException>();
    }

    [Fact]
    public async Task Add_WhenCheckoutIntentIdDuplicates_ShouldThrowDuplicatePurchaseException()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyerA = TestData.CreateUser("dup-intent-a", "dup-intent-a@example.test");
        var buyerB = TestData.CreateUser("dup-intent-b", "dup-intent-b@example.test");
        db.Users.AddRange(buyerA, buyerB);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "Intent Dup A");
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "Intent Dup B");
        db.Assets.AddRange(assetA, assetB);
        var versionA = TestData.CreateAssetVersion(assetA.Id);
        var versionB = TestData.CreateAssetVersion(assetB.Id);
        db.AssetVersions.AddRange(versionA, versionB);
        await db.SaveChangesAsync();

        var first = TestData.CreatePurchase(buyerA.Id, assetA.Id, versionA.Id, stripePaymentId: "cs_intent_first");
        TestData.AddCompletedPurchase(db, first, assetA.Title);
        await db.SaveChangesAsync();
        var sharedIntentId = first.CheckoutIntentId;
        db.ChangeTracker.Clear();

        var duplicate = TestData.CreatePurchase(buyerB.Id, assetB.Id, versionB.Id, stripePaymentId: "cs_intent_second");
        duplicate.CheckoutIntentId = sharedIntentId;

        var store = new PurchaseStore(db);
        var act = () => store.Add(duplicate);

        await act.Should().ThrowAsync<DuplicatePurchaseException>();
    }

    [Fact]
    public async Task Add_WhenUserIdAssetIdDuplicatesWithNewSession_ShouldThrowDbUpdateExceptionForUserAssetIndex()
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

        var first = TestData.CreatePurchase(buyer.Id, asset.Id, version.Id, stripePaymentId: "cs_user_asset_first");
        TestData.AddCompletedPurchase(db, first, asset.Title);
        await db.SaveChangesAsync();

        var conflict = TestData.CreatePurchase(buyer.Id, asset.Id, version.Id, stripePaymentId: "cs_user_asset_second");
        TestData.AddCompletedCheckoutIntent(db, conflict, asset.Title);
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        var store = new PurchaseStore(db);
        var act = () => store.Add(conflict);

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        var pg = ex.Which.InnerException.Should().BeOfType<PostgresException>().Subject;
        pg.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("IX_purchases_UserId_AssetId");
    }
}
