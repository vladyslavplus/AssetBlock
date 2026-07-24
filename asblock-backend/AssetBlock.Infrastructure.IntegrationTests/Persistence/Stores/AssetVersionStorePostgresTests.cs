using AssetBlock.Application.Services;
using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AssetBlock.Infrastructure.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class AssetVersionStorePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task PublishNextVersion_WhenSuccessful_ShouldIncrementVersionNumberAndFlipIsCurrent()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        var v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish/v1.bin", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);

        var draft = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish/v2.bin", versionNumber: 0, isCurrent: false);
        var published = await store.PublishNextVersion(asset.Id, author.Id, draft);

        published.VersionNumber.Should().Be(2);
        published.IsCurrent.Should().BeTrue();

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.AssetVersions.AsNoTracking().Where(v => v.AssetId == asset.Id).ToListAsync();
        rows.Should().HaveCount(2);
        rows.Single(v => v.VersionNumber == 1).IsCurrent.Should().BeFalse();
        rows.Single(v => v.VersionNumber == 2).IsCurrent.Should().BeTrue();
    }

    [Fact]
    public async Task PublishNextVersion_WhenAssetSoftDeleted_ShouldThrowAssetNotFoundException()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        var v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-deleted/v1.bin", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);
        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        var draft = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-deleted/v2.bin", versionNumber: 0, isCurrent: false);
        var act = () => store.PublishNextVersion(asset.Id, author.Id, draft);

        await act.Should().ThrowAsync<Domain.Core.Exceptions.AssetNotFoundException>();
    }

    [Fact]
    public async Task PublishNextVersion_WhenCallerIsNotAuthor_ShouldThrowUnauthorizedAccessException()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        var v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-forbidden/v1.bin", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);

        var draft = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-forbidden/v2.bin", versionNumber: 0, isCurrent: false);
        var act = () => store.PublishNextVersion(asset.Id, Guid.NewGuid(), draft);

        await act.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task PublishNextVersion_WhenCalledConcurrently_ShouldSerializeAndAssignSequentialVersionNumbers()
    {
        await using var seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var seedStore = new AssetStore(seedDb);
        var v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-race/v1.bin", versionNumber: 1);
        await seedStore.AddWithVersion(asset, v1, null);

        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var uowA = new EfUnitOfWork(dbA);
        var uowB = new EfUnitOfWork(dbB);
        var storeA = new AssetStore(dbA);
        var storeB = new AssetStore(dbB);
        var draftA = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-race/vA.bin", versionNumber: 0, isCurrent: false);
        var draftB = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/publish-race/vB.bin", versionNumber: 0, isCurrent: false);

        var taskA = uowA.ExecuteInTransaction(ct => storeA.PublishNextVersion(asset.Id, author.Id, draftA, ct));
        var taskB = uowB.ExecuteInTransaction(ct => storeB.PublishNextVersion(asset.Id, author.Id, draftB, ct));
        await Task.WhenAll(taskA, taskB);

        new[] { draftA.VersionNumber, draftB.VersionNumber }.Should().BeEquivalentTo([2, 3]);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.AssetVersions.AsNoTracking().Where(v => v.AssetId == asset.Id).ToListAsync();
        rows.Should().HaveCount(3);
        rows.Count(v => v.IsCurrent).Should().Be(1);
        rows.Single(v => v.IsCurrent).VersionNumber.Should().Be(3);
    }

    [Fact]
    public async Task AssetVersion_WhenVersionNumberDuplicatedForSameAsset_ShouldViolateUniqueConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id, storageKey: "assets/dup-number/v1.bin", versionNumber: 1));
        await db.SaveChangesAsync();

        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id, storageKey: "assets/dup-number/v1b.bin", versionNumber: 1, isCurrent: false));
        var act = () => db.SaveChangesAsync();

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        var pg = ex.Which.InnerException.Should().BeOfType<Npgsql.PostgresException>().Subject;
        pg.SqlState.Should().Be(Npgsql.PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("UIX_asset_versions_asset_number");
    }

    [Fact]
    public async Task AssetVersion_WhenTwoCurrentVersionsInsertedForSameAsset_ShouldViolateUniqueFilteredIndex()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id, storageKey: "assets/dup-current/v1.bin", versionNumber: 1));
        await db.SaveChangesAsync();

        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id, storageKey: "assets/dup-current/v2.bin", versionNumber: 2));
        var act = () => db.SaveChangesAsync();

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        var pg = ex.Which.InnerException.Should().BeOfType<Npgsql.PostgresException>().Subject;
        pg.SqlState.Should().Be(Npgsql.PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("UIX_asset_versions_asset_current");
    }

    [Fact]
    public async Task AssetVersion_WhenStorageKeyReusedAcrossAssets_ShouldViolateUniqueConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "Shared Key A");
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "Shared Key B");
        db.Assets.AddRange(assetA, assetB);
        const string sharedKey = "assets/shared-key/only-one.bin";
        db.AssetVersions.Add(TestData.CreateAssetVersion(assetA.Id, storageKey: sharedKey, versionNumber: 1));
        await db.SaveChangesAsync();

        db.AssetVersions.Add(TestData.CreateAssetVersion(assetB.Id, storageKey: sharedKey, versionNumber: 1));
        var act = () => db.SaveChangesAsync();

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        var pg = ex.Which.InnerException.Should().BeOfType<Npgsql.PostgresException>().Subject;
        pg.SqlState.Should().Be(Npgsql.PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("UIX_asset_versions_storage_key");
    }

    [Fact]
    public async Task GetAllStorageKeys_AfterMultiplePublishes_ShouldReturnKeysForEveryVersion()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        var v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/lifecycle/v1.bin", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);
        await store.PublishNextVersion(asset.Id, author.Id,
            TestData.CreateAssetVersion(asset.Id, storageKey: "assets/lifecycle/v2.bin", versionNumber: 0, isCurrent: false));
        await store.PublishNextVersion(asset.Id, author.Id,
            TestData.CreateAssetVersion(asset.Id, storageKey: "assets/lifecycle/v3.bin", versionNumber: 0, isCurrent: false));

        var keys = await store.GetAllStorageKeys(asset.Id);

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
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Snapshot Pack", price: 19.99m);
        var store = new AssetStore(db);
        var v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/snap/v1.bin", fileName: "v1.zip", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);

        var snapshot = await store.GetCurrentVersionSnapshot(asset.Id);

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
    public async Task ListVersions_WhenAssetSoftDeleted_ShouldOnlyExposeHistoryToAuthorOrPurchaser()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("history-buyer", "history-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        var v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/history/v1.bin", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, v1.Id), asset.Title);
        await db.SaveChangesAsync();
        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        (await store.ListVersions(asset.Id, includeDeletedAsset: true, requesterUserId: null)).Should().BeEmpty();
        (await store.ListVersions(asset.Id, includeDeletedAsset: true, requesterUserId: Guid.NewGuid())).Should().BeEmpty();
        (await store.ListVersions(asset.Id, includeDeletedAsset: true, requesterUserId: buyer.Id)).Should().ContainSingle();
        (await store.ListVersions(asset.Id, includeDeletedAsset: true, requesterUserId: author.Id)).Should().ContainSingle();
    }

    [Fact]
    public async Task SoftDelete_ShouldPreserveVersionRowsAndStorageKeys_HardDelete_ShouldCascadeVersions()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        var v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/delete-lifecycle/v1.bin", versionNumber: 1);
        await store.AddWithVersion(asset, v1, null);
        await store.PublishNextVersion(
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
        await using var seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        var buyer = TestData.CreateUser("pin-buyer", "pin-buyer@example.test");
        seedDb.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Pinned Pack", price: 10m);
        var seedStore = new AssetStore(seedDb);
        var v1 = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/pin/v1.bin", versionNumber: 1);
        await seedStore.AddWithVersion(asset, v1, null);

        var intentId = Guid.NewGuid();
        const string sessionId = "cs_pin_v1_price_10";
        seedDb.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyer.Id,
            AssetId = asset.Id,
            AssetVersionId = v1.Id,
            AssetTitle = asset.Title,
            UnitAmount = 10m,
            Currency = "usd",
            Status = CheckoutIntentStatus.PENDING,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        await seedDb.SaveChangesAsync();

        await seedStore.PublishNextVersion(
            asset.Id,
            author.Id,
            TestData.CreateAssetVersion(asset.Id, storageKey: "assets/pin/v2.bin", versionNumber: 0, isCurrent: false));

        await using (var priceDb = fixture.CreateDbContext())
        {
            await priceDb.Assets
                .Where(a => a.Id == asset.Id)
                .ExecuteUpdateAsync(s => s.SetProperty(a => a.Price, 20m));
        }

        await using (var preWebhook = fixture.CreateDbContext())
        {
            var current = await preWebhook.AssetVersions.AsNoTracking()
                .SingleAsync(v => v.AssetId == asset.Id && v.IsCurrent);
            current.VersionNumber.Should().Be(2);
            current.Id.Should().NotBe(v1.Id);
            var listing = await preWebhook.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id);
            listing.Price.Should().Be(20m);
        }

        var verified = new StripeCheckoutCompleted(intentId, buyer.Id, asset.Id, v1.Id, sessionId, 10m, "usd");
        var paymentService = Substitute.For<IPaymentService>();
        paymentService.VerifyCheckoutCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(verified);

        await using var db = fixture.CreateDbContext();
        var handler = CreateWebhookHandler(db, paymentService, CreateEmailComposer());
        var first = await handler.Handle(new HandleStripeWebhookCommand("payload", "sig"), CancellationToken.None);
        first.IsSuccess.Should().BeTrue();

        await using var verify = fixture.CreateDbContext();
        var purchase = await verify.Purchases.AsNoTracking().SingleAsync(p => p.AssetId == asset.Id);
        purchase.AssetVersionId.Should().Be(v1.Id);
        purchase.PricePaid.Should().Be(10m);
        purchase.Currency.Should().Be("usd");

        var second = await handler.Handle(new HandleStripeWebhookCommand("payload", "sig"), CancellationToken.None);
        second.IsSuccess.Should().BeTrue();
        (await verify.Purchases.CountAsync(p => p.AssetId == asset.Id)).Should().Be(1);
        (await verify.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.PURCHASE_COMPLETED))
            .Should().Be(1);
    }

    [Fact]
    public async Task HandleStripeWebhook_WhenSameWebhookDeliveredConcurrently_ShouldPersistExactlyOnePurchase()
    {
        await using var seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        var buyer = TestData.CreateUser("race-buyer", "race-buyer@example.test");
        seedDb.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Race Pack", price: 14.99m);
        var seedStore = new AssetStore(seedDb);
        var version = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/race/v1.bin", versionNumber: 1);
        await seedStore.AddWithVersion(asset, version, null);

        var intentId = Guid.NewGuid();
        const string sessionId = "cs_race_condition";
        seedDb.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyer.Id,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            AssetTitle = asset.Title,
            UnitAmount = asset.Price,
            Currency = "usd",
            Status = CheckoutIntentStatus.PENDING,
            CreatedAt = DateTimeOffset.UtcNow,
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1)
        });
        await seedDb.SaveChangesAsync();

        var verified = new StripeCheckoutCompleted(intentId, buyer.Id, asset.Id, version.Id, sessionId, asset.Price, "usd");
        var paymentService = Substitute.For<IPaymentService>();
        paymentService.VerifyCheckoutCompleted(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(verified);
        var emailComposer = CreateEmailComposer();
        var command = new HandleStripeWebhookCommand("payload", "sig");

        var gate = new TryCompleteRaceGate(participantCount: 2);
        var tryCompleteResults = new System.Collections.Concurrent.ConcurrentBag<bool>();

        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var handlerA = CreateWebhookHandler(
            dbA,
            paymentService,
            emailComposer,
            new GatedCheckoutIntentStore(new CheckoutIntentStore(dbA), gate, tryCompleteResults));
        var handlerB = CreateWebhookHandler(
            dbB,
            paymentService,
            emailComposer,
            new GatedCheckoutIntentStore(new CheckoutIntentStore(dbB), gate, tryCompleteResults));

        var results = await Task.WhenAll(
            handlerA.Handle(command, CancellationToken.None),
            handlerB.Handle(command, CancellationToken.None));

        results[0].IsSuccess.Should().BeTrue();
        results[1].IsSuccess.Should().BeTrue();
        tryCompleteResults.Should().BeEquivalentTo([true, false]);

        await using var verify = fixture.CreateDbContext();
        var persistedPurchase = await verify.Purchases.AsNoTracking().SingleAsync(p => p.AssetId == asset.Id);
        persistedPurchase.StripePaymentId.Should().Be(sessionId);
        var refreshedIntent = await verify.CheckoutIntents.AsNoTracking().SingleAsync(i => i.Id == intentId);
        refreshedIntent.Status.Should().Be(CheckoutIntentStatus.COMPLETED);
        (await verify.AuditLogs.CountAsync(a => a.ResourceId == persistedPurchase.Id.ToString())).Should().Be(1);
        (await verify.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.PURCHASE_COMPLETED)).Should().Be(1);
        (await verify.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.NOTIFICATION_DISPATCH)).Should().Be(3);
        (await verify.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.EMAIL_DISPATCH)).Should().Be(2);
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

    private static HandleStripeWebhookCommandHandler CreateWebhookHandler(
        ApplicationDbContext db,
        IPaymentService paymentService,
        TransactionalEmailComposer emailComposer,
        ICheckoutIntentStore? checkoutIntentStore = null) =>
        new(
            paymentService,
            new AssetStore(db),
            new PurchaseStore(db),
            checkoutIntentStore ?? new CheckoutIntentStore(db),
            new UserStore(db),
            new EfUnitOfWork(db),
            new OutboxStore(db, NullLogger<OutboxStore>.Instance),
            new AuditWriter(new AuditStore(db), new NullAuditContextAccessor(), NullLogger<AuditWriter>.Instance),
            emailComposer,
            NullLogger<HandleStripeWebhookCommandHandler>.Instance);

    /// <summary>
    /// Holds both webhook handlers at TryComplete until both arrive, so the test exercises the
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
        public Task Create(CheckoutIntent intent, CancellationToken cancellationToken = default) =>
            inner.Create(intent, cancellationToken);

        public Task<CheckoutIntent?> GetPending(Guid userId, Guid assetId, CancellationToken cancellationToken = default) =>
            inner.GetPending(userId, assetId, cancellationToken);

        public Task<CheckoutIntent?> GetById(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetById(id, cancellationToken);

        public Task<bool> HasActiveForAsset(Guid assetId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            inner.HasActiveForAsset(assetId, now, cancellationToken);

        public Task<bool> TryCancel(Guid id, CancellationToken cancellationToken = default) =>
            inner.TryCancel(id, cancellationToken);

        public Task<bool> TrySetStripeSessionId(Guid id, string stripeSessionId, CancellationToken cancellationToken = default) =>
            inner.TrySetStripeSessionId(id, stripeSessionId, cancellationToken);

        public async Task<bool> TryComplete(
            Guid id,
            Guid userId,
            Guid assetId,
            Guid assetVersionId,
            string stripeSessionId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default)
        {
            await gate.EnterAsync(cancellationToken);
            var completed = await inner.TryComplete(
                id,
                userId,
                assetId,
                assetVersionId,
                stripeSessionId,
                now,
                cancellationToken);
            tryCompleteResults.Add(completed);
            return completed;
        }
    }
}
