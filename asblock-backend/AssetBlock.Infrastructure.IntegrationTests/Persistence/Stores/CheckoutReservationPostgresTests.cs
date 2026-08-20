using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class CheckoutReservationPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CreateWithItemsAndReservations_WhenAssetOverlapsActiveReservation_ShouldThrowCheckoutItemReservedException()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("reserve-buyer", "reserve-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Shared", price: 12m);
        var other = TestData.CreateAsset(author.Id, category.Id, title: "Other", price: 8m);
        db.Assets.AddRange(asset, other);
        await db.SaveChangesAsync();
        var version = TestData.CreateAssetVersion(asset.Id);
        var otherVersion = TestData.CreateAssetVersion(other.Id);
        db.AssetVersions.AddRange(version, otherVersion);
        await db.SaveChangesAsync();

        var bundleStore = new BundleStore(db);
        var (bundle, revision) = await bundleStore.CreateWithRevision(
            author.Id,
            "Shared Bundle",
            null,
            15m,
            "usd",
            20m,
            [
                new(asset.Id, 1, asset.Title, asset.Price),
                new(other.Id, 2, other.Title, other.Price)
            ]);

        var store = new CheckoutIntentStore(db);
        var now = DateTimeOffset.UtcNow;
        var assetIntent = BuildPendingIntent(
            buyer.Id,
            assetId: asset.Id,
            bundleId: null,
            bundleRevisionId: null,
            productTitle: asset.Title,
            amount: asset.Price,
            now);
        await store.CreateWithItemsAndReservations(
            assetIntent,
            [
                BuildItem(assetIntent.Id, asset.Id, version.Id, author.Id, asset.Title, asset.Price, position: 1)
            ],
            [TestData.CreateReservation(assetIntent.Id, buyer.Id, asset.Id, expiresAt: now.AddHours(1), createdAt: now)]);

        var bundleIntent = BuildPendingIntent(
            buyer.Id,
            assetId: null,
            bundleId: bundle.Id,
            bundleRevisionId: revision.Id,
            productTitle: "Bundle with shared asset",
            amount: 15m,
            now);
        var act = () => store.CreateWithItemsAndReservations(
            bundleIntent,
            [
                BuildItem(bundleIntent.Id, asset.Id, version.Id, author.Id, asset.Title, asset.Price, position: 1),
                BuildItem(bundleIntent.Id, other.Id, otherVersion.Id, author.Id, other.Title, other.Price, position: 2)
            ],
            [
                TestData.CreateReservation(bundleIntent.Id, buyer.Id, asset.Id, expiresAt: now.AddHours(1), createdAt: now),
                TestData.CreateReservation(bundleIntent.Id, buyer.Id, other.Id, expiresAt: now.AddHours(1), createdAt: now)
            ]);

        await act.Should().ThrowAsync<CheckoutItemReservedException>();
    }

    [Fact]
    public async Task CleanupExpiredUnattachedPendingBatch_WhenReservationExpired_ShouldAllowNewCheckout()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("expire-buyer", "expire-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Expiring", price: 9m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var store = new CheckoutIntentStore(db);
        var created = DateTimeOffset.UtcNow.AddHours(-2);
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var staleIntent = BuildPendingIntent(
            buyer.Id,
            assetId: asset.Id,
            bundleId: null,
            bundleRevisionId: null,
            productTitle: asset.Title,
            amount: asset.Price,
            created,
            expiresAt: expiredAt);
        await store.CreateWithItemsAndReservations(
            staleIntent,
            [BuildItem(staleIntent.Id, asset.Id, version.Id, author.Id, asset.Title, asset.Price, position: 1)],
            [TestData.CreateReservation(staleIntent.Id, buyer.Id, asset.Id, expiresAt: expiredAt, createdAt: created)]);

        var cleaned = await store.CleanupExpiredUnattachedPendingBatch(DateTimeOffset.UtcNow, batchSize: 10);
        cleaned.Should().Be(1);

        var now = DateTimeOffset.UtcNow;
        var freshIntent = BuildPendingIntent(
            buyer.Id,
            assetId: asset.Id,
            bundleId: null,
            bundleRevisionId: null,
            productTitle: asset.Title,
            amount: asset.Price,
            now);
        var act = () => store.CreateWithItemsAndReservations(
            freshIntent,
            [BuildItem(freshIntent.Id, asset.Id, version.Id, author.Id, asset.Title, asset.Price, position: 1)],
            [TestData.CreateReservation(freshIntent.Id, buyer.Id, asset.Id, expiresAt: now.AddHours(1), createdAt: now)]);

        await act.Should().NotThrowAsync();
        (await db.CheckoutReservations.CountAsync(r => r.UserId == buyer.Id && r.AssetId == asset.Id)).Should().Be(1);
    }

    [Fact]
    public async Task CleanupExpiredUnattachedPendingBatch_WhenIntentHasStripeSession_ShouldNotCancel()    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("attached-buyer", "attached-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Attached", price: 5m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var store = new CheckoutIntentStore(db);
        var created = DateTimeOffset.UtcNow.AddHours(-2);
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var attachedIntent = BuildPendingIntent(
            buyer.Id,
            assetId: asset.Id,
            bundleId: null,
            bundleRevisionId: null,
            productTitle: asset.Title,
            amount: asset.Price,
            created,
            expiresAt: expiredAt);
        await store.CreateWithItemsAndReservations(
            attachedIntent,
            [BuildItem(attachedIntent.Id, asset.Id, version.Id, author.Id, asset.Title, asset.Price, position: 1)],
            [TestData.CreateReservation(attachedIntent.Id, buyer.Id, asset.Id, expiresAt: expiredAt, createdAt: created)]);

        // Attach a Stripe session so this intent is "attached" and must NOT be cancelled by unattached cleanup.
        await store.TrySetStripeSessionId(attachedIntent.Id, "cs_attached_test_session", CancellationToken.None);

        var cleaned = await store.CleanupExpiredUnattachedPendingBatch(DateTimeOffset.UtcNow, batchSize: 10);

        cleaned.Should().Be(0);
        var intent = await db.CheckoutIntents.AsNoTracking().SingleAsync(i => i.Id == attachedIntent.Id);
        intent.Status.Should().Be(CheckoutIntentStatus.PENDING);
    }

    [Fact]
    public async Task ClaimAttachedPendingForStripeSyncBatch_WhenYoungerThanDueBefore_ShouldNotReturn()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("young-buyer", "young-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Young", price: 7m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var store = new CheckoutIntentStore(db);
        var now = DateTimeOffset.UtcNow;
        var intent = BuildPendingIntent(
            buyer.Id,
            assetId: asset.Id,
            bundleId: null,
            bundleRevisionId: null,
            productTitle: asset.Title,
            amount: asset.Price,
            createdAt: now.AddMinutes(-1),
            expiresAt: now.AddHours(23));
        await store.CreateWithItemsAndReservations(
            intent,
            [BuildItem(intent.Id, asset.Id, version.Id, author.Id, asset.Title, asset.Price, position: 1)],
            [TestData.CreateReservation(intent.Id, buyer.Id, asset.Id, expiresAt: intent.ExpiresAt, createdAt: intent.CreatedAt)]);
        await store.TrySetStripeSessionId(intent.Id, "cs_young_attached", CancellationToken.None);

        var batch = await store.ClaimAttachedPendingForStripeSyncBatch(
            now,
            dueBefore: now.AddMinutes(-2),
            batchSize: 10);

        batch.Should().BeEmpty();
    }

    [Fact]
    public async Task ClaimAttachedPendingForStripeSyncBatch_WhenOlderThanDueBefore_ShouldClaimAndLease()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("due-buyer", "due-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Due", price: 7m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var store = new CheckoutIntentStore(db);
        var now = DateTimeOffset.UtcNow;
        var intent = BuildPendingIntent(
            buyer.Id,
            assetId: asset.Id,
            bundleId: null,
            bundleRevisionId: null,
            productTitle: asset.Title,
            amount: asset.Price,
            createdAt: now.AddMinutes(-5),
            expiresAt: now.AddHours(23));
        await store.CreateWithItemsAndReservations(
            intent,
            [BuildItem(intent.Id, asset.Id, version.Id, author.Id, asset.Title, asset.Price, position: 1)],
            [TestData.CreateReservation(intent.Id, buyer.Id, asset.Id, expiresAt: intent.ExpiresAt, createdAt: intent.CreatedAt)]);
        await store.TrySetStripeSessionId(intent.Id, "cs_due_attached", CancellationToken.None);

        var batch = await store.ClaimAttachedPendingForStripeSyncBatch(
            now,
            dueBefore: now.AddMinutes(-2),
            batchSize: 10);

        batch.Should().ContainSingle(item => item.Id == intent.Id && item.StripeSessionId == "cs_due_attached");
        var row = await db.CheckoutIntents.AsNoTracking().SingleAsync(i => i.Id == intent.Id);
        row.LastStripeReconciledAt.Should().BeCloseTo(now, TimeSpan.FromMilliseconds(1));

        var second = await store.ClaimAttachedPendingForStripeSyncBatch(
            now,
            dueBefore: now.AddMinutes(-2),
            batchSize: 10);
        second.Should().BeEmpty("claim lease must hide the row from the next worker cycle");
    }

    [Fact]
    public async Task ClaimAttachedPendingForStripeSyncBatch_WhenExpiredAndAttached_ShouldClaimIntent()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("listexp-buyer", "listexp-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Listed", price: 7m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var store = new CheckoutIntentStore(db);
        var created = DateTimeOffset.UtcNow.AddHours(-3);
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-10);
        var intent = BuildPendingIntent(
            buyer.Id,
            assetId: asset.Id,
            bundleId: null,
            bundleRevisionId: null,
            productTitle: asset.Title,
            amount: asset.Price,
            created,
            expiresAt: expiredAt);
        await store.CreateWithItemsAndReservations(
            intent,
            [BuildItem(intent.Id, asset.Id, version.Id, author.Id, asset.Title, asset.Price, position: 1)],
            [TestData.CreateReservation(intent.Id, buyer.Id, asset.Id, expiresAt: expiredAt, createdAt: created)]);
        await store.TrySetStripeSessionId(intent.Id, "cs_list_expired_test", CancellationToken.None);

        var batch = await store.ClaimAttachedPendingForStripeSyncBatch(
            DateTimeOffset.UtcNow,
            dueBefore: DateTimeOffset.UtcNow.AddMinutes(-2),
            batchSize: 10);

        batch.Should().ContainSingle(item => item.Id == intent.Id && item.StripeSessionId == "cs_list_expired_test");
    }

    [Fact]
    public async Task ClaimAttachedPendingForStripeSyncBatch_WhenTwoWorkersRace_ShouldPartitionWithoutOverlap()
    {
        await using var seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        var buyer = TestData.CreateUser("claim-race-buyer", "claim-race-buyer@example.test");
        seedDb.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Claim Race", price: 9m);
        seedDb.Assets.Add(asset);
        await seedDb.SaveChangesAsync();
        var version = TestData.CreateAssetVersion(asset.Id);
        seedDb.AssetVersions.Add(version);
        await seedDb.SaveChangesAsync();

        var seedStore = new CheckoutIntentStore(seedDb);
        var now = DateTimeOffset.UtcNow;
        var intent = BuildPendingIntent(
            buyer.Id,
            assetId: asset.Id,
            bundleId: null,
            bundleRevisionId: null,
            productTitle: asset.Title,
            amount: asset.Price,
            createdAt: now.AddMinutes(-10),
            expiresAt: now.AddHours(20));
        await seedStore.CreateWithItemsAndReservations(
            intent,
            [BuildItem(intent.Id, asset.Id, version.Id, author.Id, asset.Title, asset.Price, position: 1)],
            [TestData.CreateReservation(intent.Id, buyer.Id, asset.Id, expiresAt: intent.ExpiresAt, createdAt: intent.CreatedAt)]);
        await seedStore.TrySetStripeSessionId(intent.Id, "cs_claim_race", CancellationToken.None);

        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = new CheckoutIntentStore(dbA);
        var storeB = new CheckoutIntentStore(dbB);
        var dueBefore = now.AddMinutes(-2);

        var results = await Task.WhenAll(
            storeA.ClaimAttachedPendingForStripeSyncBatch(now, dueBefore, batchSize: 10),
            storeB.ClaimAttachedPendingForStripeSyncBatch(now, dueBefore, batchSize: 10));

        var claimed = results.SelectMany(r => r).ToList();
        claimed.Should().ContainSingle(item => item.Id == intent.Id);
        results.Count(r => r.Any(x => x.Id == intent.Id)).Should().Be(1);
    }

    [Fact]
    public async Task TouchLastStripeReconciledAt_WhenPending_ShouldUpdateTimestampAndDeferNextSync()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("touch-buyer", "touch-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Touch", price: 4m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var store = new CheckoutIntentStore(db);
        var now = DateTimeOffset.UtcNow;
        var intent = BuildPendingIntent(
            buyer.Id,
            assetId: asset.Id,
            bundleId: null,
            bundleRevisionId: null,
            productTitle: asset.Title,
            amount: asset.Price,
            createdAt: now.AddMinutes(-10),
            expiresAt: now.AddHours(20));
        await store.CreateWithItemsAndReservations(
            intent,
            [BuildItem(intent.Id, asset.Id, version.Id, author.Id, asset.Title, asset.Price, position: 1)],
            [TestData.CreateReservation(intent.Id, buyer.Id, asset.Id, expiresAt: intent.ExpiresAt, createdAt: intent.CreatedAt)]);
        await store.TrySetStripeSessionId(intent.Id, "cs_touch_test", CancellationToken.None);

        await store.TouchLastStripeReconciledAt(intent.Id, now);

        var row = await db.CheckoutIntents.AsNoTracking().SingleAsync(i => i.Id == intent.Id);
        row.LastStripeReconciledAt.Should().BeCloseTo(now, TimeSpan.FromMilliseconds(1));

        var deferred = await store.ClaimAttachedPendingForStripeSyncBatch(
            now,
            dueBefore: now.AddMinutes(-2),
            batchSize: 10);
        deferred.Should().BeEmpty();
    }

    [Fact]
    public async Task TryCancelAndRelease_WhenPendingIntent_ShouldCancelAndDeleteReservations()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("cancel-buyer", "cancel-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "CancelAsset", price: 6m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var store = new CheckoutIntentStore(db);
        var created = DateTimeOffset.UtcNow.AddHours(-2);
        var expiredAt = DateTimeOffset.UtcNow.AddMinutes(-5);
        var intent = BuildPendingIntent(
            buyer.Id,
            assetId: asset.Id,
            bundleId: null,
            bundleRevisionId: null,
            productTitle: asset.Title,
            amount: asset.Price,
            created,
            expiresAt: expiredAt);
        await store.CreateWithItemsAndReservations(
            intent,
            [BuildItem(intent.Id, asset.Id, version.Id, author.Id, asset.Title, asset.Price, position: 1)],
            [TestData.CreateReservation(intent.Id, buyer.Id, asset.Id, expiresAt: expiredAt, createdAt: created)]);

        var cancelled = await store.TryCancelAndRelease(intent.Id);

        cancelled.Should().BeTrue();
        var row = await db.CheckoutIntents.AsNoTracking().SingleAsync(i => i.Id == intent.Id);
        row.Status.Should().Be(CheckoutIntentStatus.CANCELLED);
        (await db.CheckoutReservations.CountAsync(r => r.CheckoutIntentId == intent.Id)).Should().Be(0);
    }

    private static CheckoutIntent BuildPendingIntent(
        Guid userId,
        Guid? assetId,
        Guid? bundleId,
        Guid? bundleRevisionId,
        string productTitle,
        decimal amount,
        DateTimeOffset createdAt,
        DateTimeOffset? expiresAt = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            AssetId = assetId,
            BundleId = bundleId,
            BundleRevisionId = bundleRevisionId,
            ProductTitle = productTitle,
            AmountTotal = amount,
            Currency = "usd",
            Status = CheckoutIntentStatus.PENDING,
            CreatedAt = createdAt,
            ExpiresAt = expiresAt ?? createdAt.AddHours(1)
        };

    private static CheckoutIntentItem BuildItem(
        Guid intentId,
        Guid assetId,
        Guid assetVersionId,
        Guid sellerId,
        string title,
        decimal price,
        int position) =>
        new()
        {
            Id = Guid.NewGuid(),
            CheckoutIntentId = intentId,
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            SellerId = sellerId,
            Position = position,
            AssetTitleSnapshot = title,
            VersionNumber = 1,
            ListPrice = price,
            AllocatedPrice = price,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal use",
            LicenseTerms = "terms"
        };
}
