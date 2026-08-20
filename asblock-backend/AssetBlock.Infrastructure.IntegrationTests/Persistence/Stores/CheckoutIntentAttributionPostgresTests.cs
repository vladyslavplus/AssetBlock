using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class CheckoutIntentAttributionPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CheckoutIntent_WhenCollectionAttributionIsComplete_ShouldPersist()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (Guid buyerId, Guid assetId) = await SeedBuyerAndAsset(db, "attr-ok");
        var collectionId = Guid.NewGuid();

        var intent = CreateIntent(buyerId, assetId);
        intent.AttributionSource = AnalyticsTrafficSource.COLLECTION;
        intent.AttributionCollectionId = collectionId;
        intent.AnalyticsVisitorId = Guid.NewGuid();
        intent.AnalyticsSessionId = Guid.NewGuid();
        db.CheckoutIntents.Add(intent);
        await db.SaveChangesAsync();

        var stored = await db.CheckoutIntents.AsNoTracking().SingleAsync(i => i.Id == intent.Id);
        stored.AttributionSource.Should().Be(AnalyticsTrafficSource.COLLECTION);
        stored.AttributionCollectionId.Should().Be(collectionId);
        stored.AnalyticsVisitorId.Should().Be(intent.AnalyticsVisitorId);
    }

    [Fact]
    public async Task CheckoutIntent_WhenCollectionIdHasNoCollectionSource_ShouldViolateCheckConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (Guid buyerId, Guid assetId) = await SeedBuyerAndAsset(db, "attr-src");

        var intent = CreateIntent(buyerId, assetId);
        intent.AttributionSource = AnalyticsTrafficSource.SEARCH;
        intent.AttributionCollectionId = Guid.NewGuid();
        db.CheckoutIntents.Add(intent);

        await ShouldViolateCheck(db, "CK_checkout_intents_attribution_collection");
    }

    [Fact]
    public async Task CheckoutIntent_WhenVisitorIdPresentWithoutAttributionSource_ShouldViolateCheckConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (Guid buyerId, Guid assetId) = await SeedBuyerAndAsset(db, "attr-visitor");

        var intent = CreateIntent(buyerId, assetId);
        intent.AnalyticsVisitorId = Guid.NewGuid();
        db.CheckoutIntents.Add(intent);

        await ShouldViolateCheck(db, "CK_checkout_intents_attribution_null_consistency");
    }

    [Fact]
    public async Task CheckoutIntent_WhenCollectionSourceMissingCollectionId_ShouldViolateCheckConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (Guid buyerId, Guid assetId) = await SeedBuyerAndAsset(db, "attr-coll");

        var intent = CreateIntent(buyerId, assetId);
        intent.AttributionSource = AnalyticsTrafficSource.COLLECTION;
        db.CheckoutIntents.Add(intent);

        await ShouldViolateCheck(db, "CK_checkout_intents_attribution_collection");
    }

    [Fact]
    public async Task CheckoutIntent_WhenReferrerHostHasNoExternalSource_ShouldViolateCheckConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (Guid buyerId, Guid assetId) = await SeedBuyerAndAsset(db, "attr-ref");

        var intent = CreateIntent(buyerId, assetId);
        intent.AttributionSource = AnalyticsTrafficSource.CATALOG;
        intent.AttributionReferrerHost = "blog.example.com";
        db.CheckoutIntents.Add(intent);

        await ShouldViolateCheck(db, "CK_checkout_intents_attribution_referrer_host");
    }

    [Fact]
    public async Task CheckoutIntent_WhenAttributionSourceIsUnknown_ShouldViolateCheckConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (Guid buyerId, Guid assetId) = await SeedBuyerAndAsset(db, "attr-enum");
        var intent = CreateIntent(buyerId, assetId);
        db.CheckoutIntents.Add(intent);
        await db.SaveChangesAsync();

        var act = () => db.Database.ExecuteSqlAsync(
            $"""
            UPDATE checkout_intents SET "AttributionSource" = 'NEWSLETTER' WHERE "Id" = {intent.Id}
            """);

        var ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        ex.Which.ConstraintName.Should().Be("CK_checkout_intents_AttributionSource");
    }

    private static async Task ShouldViolateCheck(ApplicationDbContext db, string constraintName)
    {
        var act = () => db.SaveChangesAsync();
        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        var pg = ex.Which.InnerException.Should().BeOfType<PostgresException>().Subject;
        pg.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        pg.ConstraintName.Should().Be(constraintName);
    }

    private static async Task<(Guid BuyerId, Guid AssetId)> SeedBuyerAndAsset(
        ApplicationDbContext db,
        string prefix)
    {
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser($"{prefix}-buyer", $"{prefix}-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Attributed Asset", price: 5m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        return (buyer.Id, asset.Id);
    }

    private static CheckoutIntent CreateIntent(Guid buyerId, Guid assetId)
    {
        var now = DateTimeOffset.UtcNow;
        return new CheckoutIntent
        {
            Id = Guid.NewGuid(),
            UserId = buyerId,
            AssetId = assetId,
            ProductTitle = "Attributed Asset",
            AmountTotal = 5m,
            Currency = "usd",
            Status = CheckoutIntentStatus.PENDING,
            CreatedAt = now,
            ExpiresAt = now.AddHours(1)
        };
    }
}
