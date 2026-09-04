using Ardalis.Result;
using AssetBlock.Application.Services;
using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Dto.Payments;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using AwesomeAssertions.Specialized;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NSubstitute;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class AssetVersionStorePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task PublishNextVersion_WhenSuccessful_ShouldIncrementVersionNumberAndFlipIsCurrent()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish/v1.bin", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);

        AssetVersion draft = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish/v2.bin", versionNumber: 0, isCurrent: false);
        AssetVersion candidate = await store.CreateNextCandidateVersion(asset.Id, author.Id, draft);

        candidate.VersionNumber.Should().Be(2);
        candidate.IsCurrent.Should().BeFalse();
        candidate.ProcessingStatus.Should().Be(AssetVersionProcessingStatus.PENDING_INSPECTION);

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        List<AssetVersion> rows = await verify.AssetVersions.AsNoTracking().Where(v => v.AssetId == asset.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(v => v.VersionNumber == 1).IsCurrent.Should().BeTrue();
        rows.Single(v => v.VersionNumber == 2).IsCurrent.Should().BeFalse();
    }

    [Fact]
    public async Task CreateNextCandidateVersion_WhenAssetSoftDeleted_ShouldThrowAssetNotFoundException()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-deleted/v1.bin", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);
        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        AssetVersion draft = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-deleted/v2.bin", versionNumber: 0, isCurrent: false);
        Func<Task> act = () => store.CreateNextCandidateVersion(asset.Id, author.Id, draft);

        await act.Should().ThrowAsync<Domain.Core.Exceptions.AssetNotFoundException>();
    }

    [Fact]
    public async Task CreateNextCandidateVersion_WhenCallerIsNotAuthor_ShouldThrowUnauthorizedAccessException()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-forbidden/v1.bin", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);

        AssetVersion draft = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-forbidden/v2.bin", versionNumber: 0, isCurrent: false);
        Func<Task> act = () => store.CreateNextCandidateVersion(asset.Id, Guid.NewGuid(), draft);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task PublishNextVersion_WhenCalledConcurrently_ShouldSerializeAndAssignSequentialVersionNumbers()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        var seedStore = new AssetStore(seedDb);
        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-race/v1.bin", versionNumber: 1);
        await seedStore.AddWithVersion(asset, v1, null);

        await using ApplicationDbContext dbA = fixture.CreateDbContext();
        await using ApplicationDbContext dbB = fixture.CreateDbContext();
        var uowA = new EfUnitOfWork(dbA);
        var uowB = new EfUnitOfWork(dbB);
        var storeA = new AssetStore(dbA);
        var storeB = new AssetStore(dbB);
        AssetVersion draftA = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-race/vA.bin", versionNumber: 0, isCurrent: false);
        AssetVersion draftB = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-race/vB.bin", versionNumber: 0, isCurrent: false);

        Task taskA = uowA.ExecuteInTransaction(ct => storeA.CreateNextCandidateVersion(asset.Id, author.Id, draftA, ct));
        Task taskB = uowB.ExecuteInTransaction(ct => storeB.CreateNextCandidateVersion(asset.Id, author.Id, draftB, ct));
        await Task.WhenAll(taskA, taskB);

        new[] { draftA.VersionNumber, draftB.VersionNumber }.Should().BeEquivalentTo([2, 3]);

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        List<AssetVersion> rows = await verify.AssetVersions.AsNoTracking().Where(v => v.AssetId == asset.Id).ToListAsync();
        rows.Should().HaveCount(3);
        rows.Count(v => v.IsCurrent).Should().Be(1);
        rows.Single(v => v.IsCurrent).VersionNumber.Should().Be(1);
    }

    [Fact]
    public async Task AssetVersion_WhenVersionNumberDuplicatedForSameAsset_ShouldViolateUniqueConstraint()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id, storageKey: "assets/dup-number/v1.bin", versionNumber: 1));
        await db.SaveChangesAsync();

        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id, storageKey: "assets/dup-number/v1b.bin", versionNumber: 1, isCurrent: false));
        Func<Task<int>> act = () => db.SaveChangesAsync();

        ExceptionAssertions<DbUpdateException> ex = await act.Should().ThrowAsync<DbUpdateException>();
        PostgresException pg = ex.Which.InnerException.Should().BeOfType<Npgsql.PostgresException>().Subject;
        pg.SqlState.Should().Be(Npgsql.PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("UIX_asset_versions_asset_number");
    }

    [Fact]
    public async Task AssetVersion_WhenTwoCurrentVersionsInsertedForSameAsset_ShouldViolateUniqueFilteredIndex()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id, storageKey: "assets/dup-current/v1.bin", versionNumber: 1));
        await db.SaveChangesAsync();

        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id, storageKey: "assets/dup-current/v2.bin", versionNumber: 2));
        Func<Task<int>> act = () => db.SaveChangesAsync();

        ExceptionAssertions<DbUpdateException> ex = await act.Should().ThrowAsync<DbUpdateException>();
        PostgresException pg = ex.Which.InnerException.Should().BeOfType<Npgsql.PostgresException>().Subject;
        pg.SqlState.Should().Be(Npgsql.PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("UIX_asset_versions_asset_current");
    }

    [Fact]
    public async Task AssetVersion_WhenStorageKeyReusedAcrossAssets_ShouldViolateUniqueConstraint()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset assetA = TestData.CreateAsset(author.Id, category.Id, title: "Shared Key A");
        Asset assetB = TestData.CreateAsset(author.Id, category.Id, title: "Shared Key B");
        db.Assets.AddRange(assetA, assetB);
        const string sharedKey = "assets/shared-key/only-one.bin";
        db.AssetVersions.Add(TestData.CreateAssetVersion(assetA.Id, storageKey: sharedKey, versionNumber: 1));
        await db.SaveChangesAsync();

        db.AssetVersions.Add(TestData.CreateAssetVersion(assetB.Id, storageKey: sharedKey, versionNumber: 1));
        Func<Task<int>> act = () => db.SaveChangesAsync();

        ExceptionAssertions<DbUpdateException> ex = await act.Should().ThrowAsync<DbUpdateException>();
        PostgresException pg = ex.Which.InnerException.Should().BeOfType<Npgsql.PostgresException>().Subject;
        pg.SqlState.Should().Be(Npgsql.PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("UIX_asset_versions_storage_key");
    }

    [Fact]
    public async Task GetAllStorageKeys_AfterMultiplePublishes_ShouldReturnKeysForEveryVersion()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/lifecycle/v1.bin", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);
        await store.CreateNextCandidateVersion(asset.Id, author.Id,
            TestData.CreateAssetVersion(asset.Id, storageKey: "assets/lifecycle/v2.bin", versionNumber: 0, isCurrent: false));
        await store.CreateNextCandidateVersion(asset.Id, author.Id,
            TestData.CreateAssetVersion(asset.Id, storageKey: "assets/lifecycle/v3.bin", versionNumber: 0, isCurrent: false));

        IReadOnlyList<string> keys = await store.GetAllStorageKeys(asset.Id);

        keys.Should().BeEquivalentTo(
        [
            "assets/lifecycle/v1.bin",
            "assets/lifecycle/v2.bin",
            "assets/lifecycle/v3.bin"
        ]);
        (await store.ExistsByStorageKey("assets/lifecycle/v2.bin")).Should().BeTrue();
        (await store.ExistsByStorageKey("assets/lifecycle/missing.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task GetCurrentVersionSnapshot_ShouldProjectVersionAndLicenseFields()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Snapshot Pack", price: 19.99m);
        var store = new AssetStore(db);
        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/snap/v1.bin", fileName: "v1.zip", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);

        AssetCurrentVersionSnapshot? snapshot = await store.GetCurrentVersionSnapshot(asset.Id);

        snapshot.Should().NotBeNull();
        snapshot.AssetId.Should().Be(asset.Id);
        snapshot.AssetVersionId.Should().Be(v1.Id);
        snapshot.AuthorId.Should().Be(author.Id);
        snapshot.VersionNumber.Should().Be(1);
        snapshot.FileName.Should().Be("v1.zip");
        snapshot.StorageKey.Should().Be("assets/snap/v1.bin");
        snapshot.LicenseCode.Should().Be(nameof(AssetLicenseCode.PERSONAL));
        snapshot.LicenseDisplayName.Should().NotBeNullOrWhiteSpace();
        snapshot.LicenseTemplateVersion.Should().NotBeNullOrWhiteSpace();
        snapshot.LicenseTerms.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ListVersions_WhenAssetDoesNotExist_ShouldReturnNull()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new AssetStore(db);

        IReadOnlyList<AssetVersionSummaryDto>? result = await store.ListVersions(Guid.NewGuid(), requesterUserId: null);
        result.Should().BeNull();
    }

    [Fact]
    public async Task ListVersions_WhenAssetHasZeroVersions_ShouldReturnEmptyList()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        await store.Add(asset);

        IReadOnlyList<AssetVersionSummaryDto>? result = await store.ListVersions(asset.Id, requesterUserId: null);
        result.Should().NotBeNull();
        result.Should().BeEmpty();
    }

    [Fact]
    public async Task ListVersions_WhenActiveAsset_AuthorSeesAllVersions_StrangerAndAnonymousSeeOnlyReadyVersions()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User stranger = TestData.CreateUser("stranger", "stranger@example.test");
        db.Users.Add(stranger);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/vis/v1.bin", versionNumber: 1, processingStatus: AssetVersionProcessingStatus.READY);
        await store.AddWithVersion(asset, v1, null);
        AssetVersion v2 = TestData.CreateAssetVersion(
            asset.Id,
            storageKey: "assets/vis/v2.bin",
            versionNumber: 2,
            isCurrent: false,
            processingStatus: AssetVersionProcessingStatus.PENDING_INSPECTION);
        db.AssetVersions.Add(v2);
        await db.SaveChangesAsync();

        IReadOnlyList<AssetVersionSummaryDto>? authorView = await store.ListVersions(asset.Id, requesterUserId: author.Id);
        authorView.Should().NotBeNull();
        authorView.Should().HaveCount(2);

        IReadOnlyList<AssetVersionSummaryDto>? strangerView = await store.ListVersions(asset.Id, requesterUserId: stranger.Id);
        strangerView.Should().NotBeNull();
        strangerView.Should().ContainSingle();
        strangerView[0].VersionNumber.Should().Be(1);

        IReadOnlyList<AssetVersionSummaryDto>? anonView = await store.ListVersions(asset.Id, requesterUserId: null);
        anonView.Should().NotBeNull();
        anonView.Should().ContainSingle();
        anonView[0].VersionNumber.Should().Be(1);
    }

    [Fact]
    public async Task ListVersions_WhenAssetSoftDeleted_ShouldOnlyExposeHistoryToAuthorOrPurchaser()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User buyer = TestData.CreateUser("history-buyer", "history-buyer@example.test");
        db.Users.Add(buyer);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/history/v1.bin", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, v1.Id), asset.Title, author.Id);
        await db.SaveChangesAsync();
        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        (await store.ListVersions(asset.Id, requesterUserId: null)).Should().BeNull();
        (await store.ListVersions(asset.Id, requesterUserId: Guid.NewGuid())).Should().BeNull();
        (await store.ListVersions(asset.Id, requesterUserId: buyer.Id)).Should().ContainSingle();
        (await store.ListVersions(asset.Id, requesterUserId: author.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task SoftDelete_ShouldPreserveVersionRowsAndStorageKeys_HardDelete_ShouldCascadeVersions()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/delete-lifecycle/v1.bin", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);
        await store.CreateNextCandidateVersion(
            asset.Id,
            author.Id,
            TestData.CreateAssetVersion(asset.Id, storageKey: "assets/delete-lifecycle/v2.bin", versionNumber: 0, isCurrent: false));

        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        (await store.GetAllStorageKeys(asset.Id)).Should().HaveCount(2);
        (await db.AssetVersions.CountAsync(v => v.AssetId == asset.Id)).Should().Be(2);
        (await store.ExistsByStorageKey("assets/delete-lifecycle/v1.bin")).Should().BeTrue();

        await store.Delete(asset.Id);

        (await db.AssetVersions.CountAsync(v => v.AssetId == asset.Id)).Should().Be(0);
        (await store.ExistsByStorageKey("assets/delete-lifecycle/v1.bin")).Should().BeFalse();
        (await store.ExistsByStorageKey("assets/delete-lifecycle/v2.bin")).Should().BeFalse();
    }

    [Fact]
    public async Task HandleStripeWebhook_WhenListingChangesAfterCheckout_ShouldPersistPinnedVersionAndPrice()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        User buyer = TestData.CreateUser("pin-buyer", "pin-buyer@example.test");
        seedDb.Users.Add(buyer);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Pinned Pack", price: 10m);
        var seedStore = new AssetStore(seedDb);
        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/pin/v1.bin", versionNumber: 1);
        await seedStore.AddWithVersion(asset, v1, null);

        var intentId = Guid.NewGuid();
        const string sessionId = "cs_pin_v1_price_10";
        SeedPendingAssetCheckout(seedDb, intentId, buyer.Id, author.Id, asset.Id, v1.Id, asset.Title, 10m);
        await seedDb.SaveChangesAsync();

        AssetVersion v2 = await seedStore.CreateNextCandidateVersion(
            asset.Id,
            author.Id,
            TestData.CreateAssetVersion(asset.Id, storageKey: "assets/pin/v2.bin", versionNumber: 0, isCurrent: false));

        await using (ApplicationDbContext promoteDb = fixture.CreateDbContext())
        {
            await promoteDb.AssetVersions
                .Where(v => v.Id == v1.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(v => v.IsCurrent, false));
            await promoteDb.AssetVersions
                .Where(v => v.Id == v2.Id)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(v => v.IsCurrent, true)
                    .SetProperty(v => v.ProcessingStatus, AssetVersionProcessingStatus.READY));
        }

        await using (ApplicationDbContext priceDb = fixture.CreateDbContext())
        {
            await priceDb.Assets
                .Where(a => a.Id == asset.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Price, 20m));
        }

        await using (ApplicationDbContext preWebhook = fixture.CreateDbContext())
        {
            AssetVersion current = await preWebhook.AssetVersions.AsNoTracking()
                .SingleAsync(v => v.AssetId == asset.Id && v.IsCurrent);
            current.VersionNumber.Should().Be(2);
            current.Id.Should().NotBe(v1.Id);
            Asset listing = await preWebhook.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id);
            listing.Price.Should().Be(20m);
        }

        var verified = new StripeCheckoutCompleted(intentId, buyer.Id, sessionId, 10m, "usd", "evt_version_snapshot_webhook");
        IPaymentService paymentService = Substitute.For<IPaymentService>();
        paymentService.VerifyCheckoutCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(verified);

        await using ApplicationDbContext db = fixture.CreateDbContext();
        HandleStripeWebhookCommandHandler handler = CreateWebhookHandler(paymentService, CreateCompletionOrchestrator(db, CreateEmailComposer()));
        Result<OrderCompletedPayload?> first = await handler.Handle(new HandleStripeWebhookCommand("payload", "sig"), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        Purchase purchase = await verify.Purchases.AsNoTracking().SingleAsync(p => p.AssetId == asset.Id);
        purchase.AssetVersionId.Should().Be(v1.Id);
        Order order = await verify.Orders.AsNoTracking().SingleAsync(o => o.StripeSessionId == sessionId);
        order.AmountPaid.Should().Be(10m);
        order.Currency.Should().Be("usd");
        OrderLine line = await verify.OrderLines.AsNoTracking().SingleAsync(l => l.OrderId == order.Id);
        line.PricePaid.Should().Be(10m);
        line.AssetVersionId.Should().Be(v1.Id);

        Result<OrderCompletedPayload?> second = await handler.Handle(new HandleStripeWebhookCommand("payload", "sig"), CancellationToken.None);
        second.IsSuccess.Should().BeTrue();
        (await verify.Purchases.CountAsync(p => p.AssetId == asset.Id)).Should().Be(1);
    }

    [Fact]
    public async Task HandleStripeWebhook_WhenDifferentEventsRaceOnIntent_ShouldPersistExactlyOnePurchase()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        User buyer = TestData.CreateUser("race-buyer", "race-buyer@example.test");
        seedDb.Users.Add(buyer);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Race Pack", price: 14.99m);
        var seedStore = new AssetStore(seedDb);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/race/v1.bin", versionNumber: 1);
        await seedStore.AddWithVersion(asset, version, null);

        var intentId = Guid.NewGuid();
        const string sessionId = "cs_race_condition";
        SeedPendingAssetCheckout(seedDb, intentId, buyer.Id, author.Id, asset.Id, version.Id, asset.Title, asset.Price);
        await seedDb.SaveChangesAsync();

        var verifiedA = new StripeCheckoutCompleted(intentId, buyer.Id, sessionId, asset.Price, "usd", "evt_intent_race_a");
        var verifiedB = new StripeCheckoutCompleted(intentId, buyer.Id, sessionId, asset.Price, "usd", "evt_intent_race_b");
        IPaymentService paymentServiceA = Substitute.For<IPaymentService>();
        paymentServiceA.VerifyCheckoutCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(verifiedA);
        IPaymentService paymentServiceB = Substitute.For<IPaymentService>();
        paymentServiceB.VerifyCheckoutCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(verifiedB);
        TransactionalEmailComposer emailComposer = CreateEmailComposer();
        var command = new HandleStripeWebhookCommand("payload", "sig");

        var gate = new TryCompleteRaceGate(participantCount: 2);
        var tryCompleteResults = new System.Collections.Concurrent.ConcurrentBag<bool>();

        await using ApplicationDbContext dbA = fixture.CreateDbContext();
        await using ApplicationDbContext dbB = fixture.CreateDbContext();
        CheckoutCompletionOrchestrator completionA = CreateCompletionOrchestrator(
            dbA,
            emailComposer,
            new GatedCheckoutIntentStore(new CheckoutIntentStore(dbA), gate, tryCompleteResults));
        CheckoutCompletionOrchestrator completionB = CreateCompletionOrchestrator(
            dbB,
            emailComposer,
            new GatedCheckoutIntentStore(new CheckoutIntentStore(dbB), gate, tryCompleteResults));
        HandleStripeWebhookCommandHandler handlerA = CreateWebhookHandler(paymentServiceA, completionA);
        HandleStripeWebhookCommandHandler handlerB = CreateWebhookHandler(paymentServiceB, completionB);

        Result<OrderCompletedPayload?>[] results = await Task.WhenAll(
            handlerA.Handle(command, CancellationToken.None),
            handlerB.Handle(command, CancellationToken.None));

        results[0].IsSuccess.Should().BeTrue();
        results[1].IsSuccess.Should().BeTrue();
        tryCompleteResults.Should().BeEquivalentTo([true, false]);

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        Order order = await verify.Orders.AsNoTracking().SingleAsync(o => o.StripeSessionId == sessionId);
        order.StripeSessionId.Should().Be(sessionId);
        (await verify.Purchases.CountAsync(p => p.AssetId == asset.Id)).Should().Be(1);
        CheckoutIntent refreshedIntent = await verify.CheckoutIntents.AsNoTracking().SingleAsync(i => i.Id == intentId);
        refreshedIntent.Status.Should().Be(CheckoutIntentStatus.COMPLETED);
        (await verify.AuditLogs.CountAsync(a => a.ResourceId == order.Id.ToString())).Should().Be(1);
        // Buyer ORDER_READY + seller ASSET_SOLD (one notification set per order).
        (await verify.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.NOTIFICATION_DISPATCH)).Should().Be(2);
        (await verify.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.EMAIL_DISPATCH)).Should().Be(2);
    }

    [Fact]
    public async Task HandleStripeWebhook_WhenSameWebhookDeliveredConcurrently_ShouldDeduplicateViaLedger()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        User buyer = TestData.CreateUser("dup-webhook-buyer", "dup-webhook-buyer@example.test");
        seedDb.Users.Add(buyer);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Dup Webhook Pack", price: 15m);
        var seedStore = new AssetStore(seedDb);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/dup-webhook/v1.bin", versionNumber: 1);
        await seedStore.AddWithVersion(asset, version, null);

        var intentId = Guid.NewGuid();
        const string sessionId = "cs_dup_webhook_concurrent";
        const string eventId = "evt_dup_webhook_concurrent_123";
        SeedPendingAssetCheckout(seedDb, intentId, buyer.Id, author.Id, asset.Id, version.Id, asset.Title, asset.Price);
        await seedDb.SaveChangesAsync();

        var verified = new StripeCheckoutCompleted(intentId, buyer.Id, sessionId, asset.Price, "usd", eventId);
        IPaymentService paymentService = Substitute.For<IPaymentService>();
        paymentService.VerifyCheckoutCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(verified);
        TransactionalEmailComposer emailComposer = CreateEmailComposer();
        var command = new HandleStripeWebhookCommand("payload", "sig");

        await using ApplicationDbContext dbA = fixture.CreateDbContext();
        await using ApplicationDbContext dbB = fixture.CreateDbContext();
        CheckoutCompletionOrchestrator completionA = CreateCompletionOrchestrator(dbA, emailComposer);
        CheckoutCompletionOrchestrator completionB = CreateCompletionOrchestrator(dbB, emailComposer);
        HandleStripeWebhookCommandHandler handlerA = CreateWebhookHandler(paymentService, completionA);
        HandleStripeWebhookCommandHandler handlerB = CreateWebhookHandler(paymentService, completionB);

        Result<OrderCompletedPayload?>[] results = await Task.WhenAll(
            handlerA.Handle(command, CancellationToken.None),
            handlerB.Handle(command, CancellationToken.None));

        results[0].IsSuccess.Should().BeTrue();
        results[1].IsSuccess.Should().BeTrue();

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        (await verify.Orders.CountAsync(o => o.StripeSessionId == sessionId)).Should().Be(1);
        (await verify.Purchases.CountAsync(p => p.AssetId == asset.Id)).Should().Be(1);
        (await verify.ProcessedStripeWebhookEvents.CountAsync(e => e.StripeEventId == eventId)).Should().Be(1);
    }

    [Fact]
    public async Task CompletePaidCheckout_WhenWebhookAndReconciliationRace_ShouldPersistExactlyOneOrder()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        User buyer = TestData.CreateUser("recon-race-buyer", "recon-race-buyer@example.test");
        seedDb.Users.Add(buyer);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Reconcile Race", price: 11m);
        var seedStore = new AssetStore(seedDb);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/recon-race/v1.bin", versionNumber: 1);
        await seedStore.AddWithVersion(asset, version, null);

        var intentId = Guid.NewGuid();
        const string sessionId = "cs_webhook_recon_race";
        SeedPendingAssetCheckout(seedDb, intentId, buyer.Id, author.Id, asset.Id, version.Id, asset.Title, asset.Price);
        await seedDb.SaveChangesAsync();
        await seedDb.CheckoutIntents
            .Where(i => i.Id == intentId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.StripeSessionId, sessionId));

        var webhookVerified = new StripeCheckoutCompleted(intentId, buyer.Id, sessionId, asset.Price, "usd", "evt_webhook_recon_race");
        var reconcileVerified = new StripeCheckoutCompleted(intentId, buyer.Id, sessionId, asset.Price, "usd");
        IPaymentService paymentService = Substitute.For<IPaymentService>();
        paymentService.VerifyCheckoutCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(webhookVerified);
        paymentService.GetCheckoutSession(sessionId, Arg.Any<CancellationToken>())
            .Returns(new StripeCheckoutSessionSnapshot(
                sessionId,
                StripeConstants.CheckoutSessionStatuses.COMPLETE,
                null,
                reconcileVerified));
        TransactionalEmailComposer emailComposer = CreateEmailComposer();

        var gate = new TryCompleteRaceGate(participantCount: 2);
        var tryCompleteResults = new System.Collections.Concurrent.ConcurrentBag<bool>();

        await using ApplicationDbContext dbWebhook = fixture.CreateDbContext();
        await using ApplicationDbContext dbReconcile = fixture.CreateDbContext();
        var gatedStoreWebhook = new GatedCheckoutIntentStore(
            new CheckoutIntentStore(dbWebhook),
            gate,
            tryCompleteResults);
        var gatedStoreReconcile = new GatedCheckoutIntentStore(
            new CheckoutIntentStore(dbReconcile),
            gate,
            tryCompleteResults);
        CheckoutCompletionOrchestrator completionWebhook = CreateCompletionOrchestrator(
            dbWebhook,
            emailComposer,
            gatedStoreWebhook);
        CheckoutCompletionOrchestrator reconcileCompletion = CreateCompletionOrchestrator(
            dbReconcile,
            emailComposer,
            gatedStoreReconcile);
        HandleStripeWebhookCommandHandler webhookHandler = CreateWebhookHandler(paymentService, completionWebhook);

        Task<Result<OrderCompletedPayload?>> webhookTask = webhookHandler.Handle(
            new HandleStripeWebhookCommand("payload", "sig"),
            CancellationToken.None);
        Task<OrderCompletedPayload?> reconcileTask = reconcileCompletion.CompletePaidCheckout(reconcileVerified, CancellationToken.None);
        await Task.WhenAll(webhookTask, reconcileTask);

        (await webhookTask).IsSuccess.Should().BeTrue();
        tryCompleteResults.Should().BeEquivalentTo([true, false]);

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        (await verify.Orders.CountAsync(o => o.StripeSessionId == sessionId)).Should().Be(1);
        (await verify.Purchases.CountAsync(p => p.AssetId == asset.Id)).Should().Be(1);
        (await verify.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.NOTIFICATION_DISPATCH)).Should().Be(2);
        (await verify.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.EMAIL_DISPATCH)).Should().Be(2);
    }

    private static void SeedPendingAssetCheckout(
        ApplicationDbContext db,
        Guid intentId,
        Guid buyerId,
        Guid sellerId,
        Guid assetId,
        Guid assetVersionId,
        string title,
        decimal amount)
    {
        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyerId,
            AssetId = assetId,
            ProductTitle = title,
            AmountTotal = amount,
            Currency = "usd",
            Status = CheckoutIntentStatus.PENDING,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        db.CheckoutIntentItems.Add(new CheckoutIntentItem
        {
            Id = Guid.NewGuid(),
            CheckoutIntentId = intentId,
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            SellerId = sellerId,
            Position = 1,
            AssetTitleSnapshot = title,
            VersionNumber = 1,
            ListPrice = amount,
            AllocatedPrice = amount,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal use",
            LicenseTerms = "terms"
        });
    }

    private static TransactionalEmailComposer CreateEmailComposer() =>
        new(Microsoft.Extensions.Options.Options.Create(new EmailOptions
        {
            Provider = "Smtp",
            FromName = "AssetBlock",
            FromAddress = "noreply@localhost",
            PublicAppBaseUrl = "http://localhost:3000",
            MessageIdDomain = "mail.localhost",
            Smtp = new EmailSmtpOptions { Host = "localhost", Port = 1025, Security = SmtpSecurityMode.NONE, TimeoutSeconds = 30 }
        }));

    private static CheckoutCompletionOrchestrator CreateCompletionOrchestrator(
        ApplicationDbContext db,
        TransactionalEmailComposer emailComposer,
        ICheckoutIntentStore? checkoutIntentStore = null) =>
        new(
            new AssetStore(db),
            new BundleStore(db),
            new OrderStore(db),
            checkoutIntentStore ?? new CheckoutIntentStore(db),
            new UserStore(db),
            new ProcessedStripeWebhookEventStore(db, NullLogger<ProcessedStripeWebhookEventStore>.Instance),
            new EfUnitOfWork(db),
            new OutboxStore(db, NullLogger<OutboxStore>.Instance),
            new AuditWriter(new AuditStore(db), new NullAuditContextAccessor(), NullLogger<AuditWriter>.Instance),
            emailComposer,
            TimeProvider.System,
            NullLogger<CheckoutCompletionOrchestrator>.Instance);

    private static HandleStripeWebhookCommandHandler CreateWebhookHandler(
        IPaymentService paymentService,
        ICheckoutCompletionService completionService) =>
        new(
            paymentService,
            completionService,
            NullLogger<HandleStripeWebhookCommandHandler>.Instance);

    /// <summary>
    /// Holds both webhook handlers at TryCompleteAndRelease until both arrive, so the test exercises the
    /// PostgreSQL conditional-update race instead of a sequential early-idempotent hit.
    /// </summary>
    private sealed class TryCompleteRaceGate(int participantCount)
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) >= participantCount)
            {
                _ready.TrySetResult();
            }

            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

    private sealed class GatedCheckoutIntentStore(
        ICheckoutIntentStore inner,
        TryCompleteRaceGate gate,
        System.Collections.Concurrent.ConcurrentBag<bool> tryCompleteResults) : ICheckoutIntentStore
    {
        public Task CreateWithItemsAndReservations(
            CheckoutIntent intent,
            IReadOnlyList<CheckoutIntentItem> items,
            IReadOnlyList<CheckoutReservation> reservations,
            CancellationToken cancellationToken = default) =>
            inner.CreateWithItemsAndReservations(intent, items, reservations, cancellationToken);

        public Task<CheckoutIntent?> GetPendingForAsset(Guid userId, Guid assetId, CancellationToken cancellationToken = default) =>
            inner.GetPendingForAsset(userId, assetId, cancellationToken);

        public Task<CheckoutIntent?> GetPendingForBundle(Guid userId, Guid bundleId, CancellationToken cancellationToken = default) =>
            inner.GetPendingForBundle(userId, bundleId, cancellationToken);

        public Task<CheckoutIntent?> GetByIdWithItems(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetByIdWithItems(id, cancellationToken);

        public Task ReleaseExpiredReservations(
            Guid userId,
            IReadOnlyList<Guid> assetIds,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            inner.ReleaseExpiredReservations(userId, assetIds, now, cancellationToken);

        public Task<bool> HasActiveForAsset(Guid assetId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            inner.HasActiveForAsset(assetId, now, cancellationToken);

        public Task<bool> TryCancelAndRelease(Guid id, CancellationToken cancellationToken = default) =>
            inner.TryCancelAndRelease(id, cancellationToken);

        public Task<bool> TrySetStripeSessionId(Guid id, string stripeSessionId, CancellationToken cancellationToken = default) =>
            inner.TrySetStripeSessionId(id, stripeSessionId, cancellationToken);

        public async Task<bool> TryCompleteAndRelease(
            Guid id,
            Guid userId,
            string stripeSessionId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            await gate.EnterAsync(cancellationToken);
            var completed = await inner.TryCompleteAndRelease(id, userId, stripeSessionId, now, cancellationToken);
            tryCompleteResults.Add(completed);
            return completed;
        }

        public Task<int> CleanupExpiredUnattachedPendingBatch(
            DateTimeOffset now,
            int batchSize,
            CancellationToken cancellationToken = default) =>
            inner.CleanupExpiredUnattachedPendingBatch(now, batchSize, cancellationToken);

        public Task<IReadOnlyList<(Guid Id, string StripeSessionId)>> ClaimAttachedPendingForStripeSyncBatch(
            DateTimeOffset now,
            DateTimeOffset dueBefore,
            int batchSize,
            CancellationToken cancellationToken = default) =>
            inner.ClaimAttachedPendingForStripeSyncBatch(now, dueBefore, batchSize, cancellationToken);

        public Task TouchLastStripeReconciledAt(
            Guid id,
            DateTimeOffset reconciledAt,
            CancellationToken cancellationToken = default) =>
            inner.TouchLastStripeReconciledAt(id, reconciledAt, cancellationToken);

        public Task DeleteTerminalUnpaidReferencingAsset(Guid assetId, CancellationToken cancellationToken = default) =>
            inner.DeleteTerminalUnpaidReferencingAsset(assetId, cancellationToken);
    }
}
