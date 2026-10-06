using AssetBlock.Application.Services;
using AssetBlock.Application.UseCases.Assets.DeleteAsset;
using AssetBlock.Application.UseCases.Payments.HandleStripeWebhook;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Dto.Payments;
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
public sealed class CheckoutCompletionPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CompletePaidCheckout_WhenCancelWinsBeforeFulfillmentLock_ShouldPersistSingleHoldWithoutEntitlement()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        (CheckoutSeed seed, StripeCheckoutCompleted verified) = await SeedPendingPaidCheckoutAsync(seedDb);

        var cancelGate = new FulfillmentLockBarrier();
        TransactionalEmailComposer emailComposer = CreateEmailComposer();
        await using ApplicationDbContext db = fixture.CreateDbContext();
        CheckoutCompletionOrchestrator orchestrator = CreateCompletionOrchestrator(
            db,
            emailComposer,
            new CancelBeforeFulfillmentLockStore(new CheckoutIntentStore(db), cancelGate));

        Task<OrderCompletedPayload?> completionTask = orchestrator.CompletePaidCheckout(verified, CancellationToken.None);
        await cancelGate.WaitUntilBlockedOnLockAsync();

        await using ApplicationDbContext cancelDb = fixture.CreateDbContext();
        (await new CheckoutIntentStore(cancelDb).TryCancelAndRelease(seed.IntentId, CancellationToken.None)).Should().BeTrue();
        cancelGate.ReleaseLockWaiters();

        OrderCompletedPayload? payload = await completionTask;
        payload.Should().BeNull();

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        (await verify.Purchases.CountAsync(p => p.AssetId == seed.Asset.Id)).Should().Be(0);
        (await verify.Orders.CountAsync(o => o.StripeSessionId == seed.SessionId)).Should().Be(0);
        PaidCheckoutReconciliationHold hold = await verify.PaidCheckoutReconciliationHolds
            .AsNoTracking()
            .SingleAsync(h => h.CheckoutIntentId == seed.IntentId);
        hold.State.Should().Be(PaidCheckoutReconciliationState.HELD);
    }

    [Fact]
    public async Task CompletePaidCheckout_WhenProcessedEventExistsWithoutOrderOrHold_ShouldRestoreHoldOnReplay()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        (CheckoutSeed seed, StripeCheckoutCompleted verified) = await SeedPendingPaidCheckoutAsync(seedDb);
        await seedDb.CheckoutIntents
            .Where(i => i.Id == seed.IntentId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, CheckoutIntentStatus.CANCELLED));
        seedDb.ProcessedStripeWebhookEvents.Add(new ProcessedStripeWebhookEvent
        {
            Id = Guid.NewGuid(),
            StripeEventId = verified.StripeEventId,
            EventType = StripeConstants.Events.CHECKOUT_SESSION_COMPLETED,
            ProcessedAt = DateTimeOffset.UtcNow
        });
        await seedDb.SaveChangesAsync();

        TransactionalEmailComposer emailComposer = CreateEmailComposer();
        await using ApplicationDbContext db = fixture.CreateDbContext();
        CheckoutCompletionOrchestrator orchestrator = CreateCompletionOrchestrator(db, emailComposer);

        OrderCompletedPayload? payload = await orchestrator.CompletePaidCheckout(verified, CancellationToken.None);
        payload.Should().BeNull();

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        (await verify.Purchases.CountAsync(p => p.AssetId == seed.Asset.Id)).Should().Be(0);
        (await verify.PaidCheckoutReconciliationHolds.CountAsync(h =>
            h.CheckoutIntentId == seed.IntentId && h.State == PaidCheckoutReconciliationState.HELD)).Should().Be(1);
    }

    [Fact]
    public async Task CompletePaidCheckout_WhenTwoDifferentEventIdsRace_ShouldPersistSingleOrderOrHold()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        (CheckoutSeed seed, _) = await SeedPendingPaidCheckoutAsync(seedDb);
        await seedDb.CheckoutIntents
            .Where(i => i.Id == seed.IntentId)
            .ExecuteUpdateAsync(s => s.SetProperty(i => i.Status, CheckoutIntentStatus.CANCELLED));

        var verifiedA = new StripeCheckoutCompleted(
            seed.IntentId,
            seed.Buyer.Id,
            seed.SessionId,
            seed.Asset.Price,
            "usd",
            "evt_checkout_race_a");
        var verifiedB = new StripeCheckoutCompleted(
            seed.IntentId,
            seed.Buyer.Id,
            seed.SessionId,
            seed.Asset.Price,
            "usd",
            "evt_checkout_race_b");

        TransactionalEmailComposer emailComposer = CreateEmailComposer();

        await using ApplicationDbContext dbA = fixture.CreateDbContext();
        await using ApplicationDbContext dbB = fixture.CreateDbContext();
        CheckoutCompletionOrchestrator orchestratorA = CreateCompletionOrchestrator(dbA, emailComposer);
        CheckoutCompletionOrchestrator orchestratorB = CreateCompletionOrchestrator(dbB, emailComposer);

        await Task.WhenAll(
            orchestratorA.CompletePaidCheckout(verifiedA, CancellationToken.None),
            orchestratorB.CompletePaidCheckout(verifiedB, CancellationToken.None));

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        (await verify.Orders.CountAsync(o => o.StripeSessionId == seed.SessionId)).Should().BeLessThanOrEqualTo(1);
        (await verify.Purchases.CountAsync(p => p.AssetId == seed.Asset.Id)).Should().BeLessThanOrEqualTo(1);
        List<PaidCheckoutReconciliationHold> holds = await verify.PaidCheckoutReconciliationHolds
            .AsNoTracking()
            .Where(h => h.CheckoutIntentId == seed.IntentId && h.State == PaidCheckoutReconciliationState.HELD)
            .ToListAsync();
        holds.Should().HaveCountLessThanOrEqualTo(1);
        (holds.Count == 1 || await verify.Orders.AnyAsync(o => o.StripeSessionId == seed.SessionId))
            .Should().BeTrue();
    }

    [Fact]
    public async Task CompletePaidCheckout_AfterProviderBoundCancelAndSellerSoftDelete_ShouldHoldWithoutBlobDelete()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        (CheckoutSeed seed, StripeCheckoutCompleted verified) = await SeedPendingPaidCheckoutAsync(seedDb);
        await seedDb.CheckoutIntents
            .Where(i => i.Id == seed.IntentId)
            .ExecuteUpdateAsync(s =>
                s.SetProperty(i => i.StripeSessionId, seed.SessionId)
                    .SetProperty(i => i.Status, CheckoutIntentStatus.CANCELLED));

        var deleteHandler = new DeleteAssetCommandHandler(
            new AssetStore(seedDb),
            new PurchaseStore(seedDb),
            new CheckoutIntentStore(seedDb),
            new CheckoutReconciliationHoldStore(seedDb),
            new EfUnitOfWork(seedDb),
            new OutboxStore(seedDb, NullLogger<OutboxStore>.Instance),
            new AuditWriter(new AuditStore(seedDb), new NullAuditContextAccessor(), NullLogger<AuditWriter>.Instance),
            Substitute.For<ICacheService>(),
            NullLogger<DeleteAssetCommandHandler>.Instance);
        (await deleteHandler.Handle(new DeleteAssetCommand(seed.Asset.Id, seed.Author.Id), CancellationToken.None))
            .IsSuccess.Should().BeTrue();

        TransactionalEmailComposer emailComposer = CreateEmailComposer();
        await using ApplicationDbContext db = fixture.CreateDbContext();
        CheckoutCompletionOrchestrator orchestrator = CreateCompletionOrchestrator(db, emailComposer);

        OrderCompletedPayload? payload = await orchestrator.CompletePaidCheckout(verified, CancellationToken.None);
        payload.Should().BeNull();

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        Asset assetRow = await verify.Assets.AsNoTracking().SingleAsync(a => a.Id == seed.Asset.Id);
        assetRow.DeletedAt.Should().NotBeNull();
        (await verify.CheckoutIntentItems.CountAsync(i => i.CheckoutIntentId == seed.IntentId)).Should().Be(1);
        (await verify.Purchases.CountAsync(p => p.AssetId == seed.Asset.Id)).Should().Be(0);
        (await verify.PaidCheckoutReconciliationHolds.CountAsync(h =>
            h.CheckoutIntentId == seed.IntentId && h.State == PaidCheckoutReconciliationState.HELD)).Should().Be(1);
        (await verify.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.ASSET_BLOB_DELETE)).Should().Be(0);
    }

    private sealed record CheckoutSeed(
        Guid IntentId,
        string SessionId,
        User Buyer,
        User Author,
        Asset Asset,
        AssetVersion Version);

    private static async Task<(CheckoutSeed Seed, StripeCheckoutCompleted Verified)> SeedPendingPaidCheckoutAsync(
        ApplicationDbContext seedDb)
    {
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        User buyer = TestData.CreateUser("checkout-buyer", $"checkout-{Guid.NewGuid():N}@example.test");
        seedDb.Users.Add(buyer);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Checkout Pack", price: 12.50m);
        var seedStore = new AssetStore(seedDb);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, storageKey: "assets/checkout/v1.bin", versionNumber: 1);
        await seedStore.AddWithVersion(asset, version, null);
        PublicationSnapshot offeringSnapshot = await ApprovedPublicationTestBuilder.AttachTrustedApprovedPublication(
            seedDb,
            asset,
            version,
            author,
            category);

        var intentId = Guid.NewGuid();
        const string sessionId = "cs_checkout_regression";
        SeedPendingAssetCheckout(seedDb, intentId, buyer.Id, author.Id, asset.Id, version.Id, asset.Title, asset.Price, offeringSnapshot.Id);
        await seedDb.SaveChangesAsync();

        var verified = new StripeCheckoutCompleted(
            intentId,
            buyer.Id,
            sessionId,
            asset.Price,
            "usd",
            $"evt_checkout_{Guid.NewGuid():N}");

        return (new CheckoutSeed(intentId, sessionId, buyer, author, asset, version), verified);
    }

    private static void SeedPendingAssetCheckout(
        ApplicationDbContext db,
        Guid intentId,
        Guid buyerId,
        Guid sellerId,
        Guid assetId,
        Guid assetVersionId,
        string title,
        decimal amount,
        Guid publicationSnapshotId)
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
            PublicationSnapshotId = publicationSnapshotId,
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
            new CheckoutReconciliationHoldStore(db),
            new UserStore(db),
            new ProcessedStripeWebhookEventStore(db, NullLogger<ProcessedStripeWebhookEventStore>.Instance),
            new EfUnitOfWork(db),
            new AuditWriter(new AuditStore(db), new NullAuditContextAccessor(), NullLogger<AuditWriter>.Instance),
            new CheckoutOrderFactory(),
            new CheckoutNotificationPublisher(
                new OutboxStore(db, NullLogger<OutboxStore>.Instance),
                emailComposer,
                NullLogger<CheckoutNotificationPublisher>.Instance),
            TimeProvider.System,
            NullLogger<CheckoutCompletionOrchestrator>.Instance);

    private sealed class FulfillmentLockBarrier
    {
        private readonly TaskCompletionSource _blockedOnLock = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task WaitUntilBlockedOnLockAsync() => _blockedOnLock.Task;

        public void ReleaseLockWaiters() => _release.TrySetResult();

        public Task WaitForReleaseAsync() => _release.Task;

        public void SignalBlockedOnLock() => _blockedOnLock.TrySetResult();
    }

    private sealed class CancelBeforeFulfillmentLockStore(
        ICheckoutIntentStore inner,
        FulfillmentLockBarrier barrier) : ICheckoutIntentStore
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

        public Task<bool> TryCompleteAndRelease(
            Guid id,
            Guid userId,
            string stripeSessionId,
            DateTimeOffset now,
            CancellationToken cancellationToken = default) =>
            inner.TryCompleteAndRelease(id, userId, stripeSessionId, now, cancellationToken);

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

        public async Task<CheckoutIntent?> LockForFulfillment(Guid id, CancellationToken cancellationToken = default)
        {
            barrier.SignalBlockedOnLock();
            await barrier.WaitForReleaseAsync().WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            return await inner.LockForFulfillment(id, cancellationToken);
        }

        public Task<bool> HasProviderBoundUnresolvedCheckoutReference(Guid assetId, CancellationToken cancellationToken = default) =>
            inner.HasProviderBoundUnresolvedCheckoutReference(assetId, cancellationToken);
    }

    private sealed class TryCompleteRaceGate(int participantCount)
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task Enter(CancellationToken cancellationToken)
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

        public async Task<CheckoutIntent?> LockForFulfillment(Guid id, CancellationToken cancellationToken = default)
        {
            await gate.Enter(cancellationToken);
            return await inner.LockForFulfillment(id, cancellationToken);
        }

        public Task<bool> HasProviderBoundUnresolvedCheckoutReference(Guid assetId, CancellationToken cancellationToken = default) =>
            inner.HasProviderBoundUnresolvedCheckoutReference(assetId, cancellationToken);
    }
}
