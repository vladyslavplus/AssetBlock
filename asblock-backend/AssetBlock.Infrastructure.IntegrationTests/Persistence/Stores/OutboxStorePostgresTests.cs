using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Email;
using AssetBlock.Domain.Core.Dto.Outbox;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class OutboxStorePostgresTests(PostgresFixture fixture)
{
    private static OutboxStore CreateStore(ApplicationDbContext db) =>
        new(db, NullLogger<OutboxStore>.Instance);

    [Fact]
    public async Task ClaimPendingBatch_WhenTwoContextsClaimConcurrently_ShouldNotOverlap()
    {
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        OutboxStore seedStore = CreateStore(seedDb);
        for (var i = 0; i < 20; i++)
        {
            await seedStore.Enqueue(OutboxMessageTypes.ORDER_COMPLETED, new { i }, CancellationToken.None);
        }

        await using ApplicationDbContext dbA = fixture.CreateDbContext();
        await using ApplicationDbContext dbB = fixture.CreateDbContext();
        OutboxStore storeA = CreateStore(dbA);
        OutboxStore storeB = CreateStore(dbB);

        Task<IReadOnlyList<OutboxMessage>> claimATask = storeA.ClaimPendingBatch(10, TimeSpan.FromMinutes(5));
        Task<IReadOnlyList<OutboxMessage>> claimBTask = storeB.ClaimPendingBatch(10, TimeSpan.FromMinutes(5));
        await Task.WhenAll(claimATask, claimBTask);

        var idsA = (await claimATask).Select(m => m.Id).ToHashSet();
        var idsB = (await claimBTask).Select(m => m.Id).ToHashSet();

        idsA.Should().HaveCount(10);
        idsB.Should().HaveCount(10);
        idsA.Intersect(idsB).Should().BeEmpty();
    }

    [Fact]
    public async Task ExecuteInTransaction_WhenActionThrows_ShouldRollBackBusinessRowAndOutbox()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var unitOfWork = new EfUnitOfWork(db);
        OutboxStore outbox = CreateStore(db);
        Guid assetId = asset.Id;
        var originalTitle = asset.Title;

        Func<Task> act = async () => await unitOfWork.ExecuteInTransaction(async ct =>
        {
            asset.Title = "mutated-in-tx";
            await db.SaveChangesAsync(ct);
            await outbox.Enqueue(
                OutboxMessageTypes.ASSET_BLOB_DELETE,
                new AssetBlobDeletePayload(assetId, "assets/rollback-test.bin"),
                ct);
            throw new InvalidOperationException("force rollback");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        (await verify.OutboxMessages.CountAsync()).Should().Be(0);
        Asset reloaded = await verify.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId);
        reloaded.Title.Should().Be(originalTitle);
    }

    [Fact]
    public async Task ExecuteInTransaction_WhenEmailDispatchEnqueuedAndThrows_ShouldRollBackPurchaseAndEmailRow()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User buyer = TestData.CreateUser("buyer-email-tx", "buyer-email-tx@example.com");
        db.Users.Add(buyer);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var unitOfWork = new EfUnitOfWork(db);
        OutboxStore outbox = CreateStore(db);
        var purchaseId = Guid.NewGuid();

        Func<Task> act = async () => await unitOfWork.ExecuteInTransaction(async ct =>
        {
            var purchase = new Purchase
            {
                Id = purchaseId,
                UserId = buyer.Id,
                AssetId = asset.Id,
                AssetVersionId = version.Id,
                OrderLineId = Guid.NewGuid(),
                PurchasedAt = DateTimeOffset.UtcNow
            };
            TestData.AddCompletedPurchase(db, purchase, asset.Title, author.Id, stripeSessionId: "cs_email_rollback");
            await db.SaveChangesAsync(ct);
            await outbox.Enqueue(
                OutboxMessageTypes.EMAIL_DISPATCH,
                new EmailDispatchPayload(
                    buyer.Email,
                    buyer.Id,
                    EmailTemplateKind.PURCHASE_RECEIPT,
                    "Purchase receipt: Pack",
                    "text body without secrets",
                    "<p>html body without secrets</p>"),
                ct);
            throw new InvalidOperationException("force email rollback");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        (await verify.Purchases.CountAsync(p => p.Id == purchaseId)).Should().Be(0);
        (await verify.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.EMAIL_DISPATCH)).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteInTransaction_WhenEmailDispatchCommits_ShouldPersistSafePayloadWithoutSecrets()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User buyer = TestData.CreateUser("buyer-email-ok", "buyer-email-ok@example.com");
        db.Users.Add(buyer);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var unitOfWork = new EfUnitOfWork(db);
        OutboxStore outbox = CreateStore(db);
        var purchaseId = Guid.NewGuid();
        var payload = new EmailDispatchPayload(
            buyer.Email,
            buyer.Id,
            EmailTemplateKind.PURCHASE_RECEIPT,
            "Purchase receipt: Pack",
            "Asset purchased. Open http://localhost:3000/library",
            "<p>Asset purchased</p>");

        await unitOfWork.ExecuteInTransaction(async ct =>
        {
            var purchase = new Purchase
            {
                Id = purchaseId,
                UserId = buyer.Id,
                AssetId = asset.Id,
                AssetVersionId = version.Id,
                OrderLineId = Guid.NewGuid(),
                PurchasedAt = DateTimeOffset.UtcNow
            };
            TestData.AddCompletedPurchase(db, purchase, asset.Title, author.Id, stripeSessionId: "cs_email_commit");
            await db.SaveChangesAsync(ct);
            await outbox.Enqueue(OutboxMessageTypes.EMAIL_DISPATCH, payload, ct);
        });

        await using ApplicationDbContext verify = fixture.CreateDbContext();
        (await verify.Purchases.CountAsync(p => p.Id == purchaseId)).Should().Be(1);
        OutboxMessage row = await verify.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Type == OutboxMessageTypes.EMAIL_DISPATCH);
        row.Payload.Should().Contain("\"templateKind\":\"PURCHASE_RECEIPT\"");
        row.Payload.Should().NotContain("\"templateKind\":0");
        row.Payload.Should().Contain("http://localhost:3000/library");
        row.Payload.Should().Contain(buyer.Email);
        row.Payload.Should().NotContain("sk_live");
        row.Payload.Should().NotContain("whsec_");
        row.Payload.Should().NotContain(version.StorageKey);
        row.Payload.Should().NotContain("Password");
        row.Payload.Should().NotContain("cs_email_commit");
    }

    [Fact]
    public async Task ClaimAndMark_WhenLeaseExpires_StaleWorkerCannotMutateNewLease()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        OutboxStore store = CreateStore(db);
        await store.Enqueue(
            OutboxMessageTypes.ASSET_BLOB_DELETE,
            new AssetBlobDeletePayload(Guid.NewGuid(), "key"),
            CancellationToken.None);

        IReadOnlyList<OutboxMessage> first = await store.ClaimPendingBatch(1, TimeSpan.FromMilliseconds(50));
        first.Should().HaveCount(1);
        OutboxMessage stale = first[0];
        stale.LockToken.Should().NotBeNull();

        await Task.Delay(80);

        await using ApplicationDbContext db2 = fixture.CreateDbContext();
        OutboxStore store2 = CreateStore(db2);
        IReadOnlyList<OutboxMessage> second = await store2.ClaimPendingBatch(1, TimeSpan.FromMinutes(5));
        second.Should().HaveCount(1);
        OutboxMessage fresh = second[0];
        fresh.Id.Should().Be(stale.Id);
        fresh.LockToken.Should().HaveValue();
        fresh.LockToken!.Value.Should().NotBe(stale.LockToken!.Value);

        (await store.MarkProcessed(stale.Id, stale.LockToken!.Value)).Should().BeFalse();
        (await store2.MarkProcessed(fresh.Id, fresh.LockToken!.Value)).Should().BeTrue();

        OutboxMessage row = await db2.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == fresh.Id);
        row.ProcessedAt.Should().NotBeNull();
        row.LockToken.Should().BeNull();
    }

    [Fact]
    public async Task RenewLease_WhenClaimIsCurrent_ShouldPreventReclaimUntilExtendedLeaseExpires()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        OutboxStore store = CreateStore(db);
        await store.Enqueue(
            OutboxMessageTypes.ASSET_BLOB_DELETE,
            new AssetBlobDeletePayload(Guid.NewGuid(), "assets/renew-test.bin"));

        IReadOnlyList<OutboxMessage> claimed = await store.ClaimPendingBatch(1, TimeSpan.FromMilliseconds(200));
        OutboxMessage message = claimed.Should().ContainSingle().Subject;
        Guid lockToken = message.LockToken!.Value;

        await Task.Delay(75);
        (await store.RenewLease(message.Id, lockToken, TimeSpan.FromMilliseconds(300))).Should().BeTrue();
        await Task.Delay(175);

        await using ApplicationDbContext competingDb = fixture.CreateDbContext();
        OutboxStore competingStore = CreateStore(competingDb);
        (await competingStore.ClaimPendingBatch(1, TimeSpan.FromMinutes(5))).Should().BeEmpty();

        await Task.Delay(175);
        IReadOnlyList<OutboxMessage> reclaimed = await competingStore.ClaimPendingBatch(1, TimeSpan.FromMinutes(5));
        reclaimed.Should().ContainSingle();
        reclaimed[0].Id.Should().Be(message.Id);
        reclaimed[0].LockToken.Should().NotBe(lockToken);
        (await store.RenewLease(message.Id, lockToken, TimeSpan.FromMinutes(5))).Should().BeFalse();
    }

    [Fact]
    public async Task MarkFailed_WhenRetryIsDue_ShouldMakeSameMessageClaimableAgain()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        OutboxStore store = CreateStore(db);
        await store.Enqueue(
            OutboxMessageTypes.ASSET_BLOB_DELETE,
            new AssetBlobDeletePayload(Guid.NewGuid(), "assets/retry-test.bin"),
            CancellationToken.None);

        IReadOnlyList<OutboxMessage> first = await store.ClaimPendingBatch(1, TimeSpan.FromMinutes(5));
        first.Should().ContainSingle();
        OutboxMessage claimed = first[0];
        (await store.MarkFailed(
            claimed.Id,
            claimed.LockToken!.Value,
            "transient failure",
            DateTimeOffset.UtcNow.AddMilliseconds(-1))).Should().BeTrue();

        IReadOnlyList<OutboxMessage> retry = await store.ClaimPendingBatch(1, TimeSpan.FromMinutes(5));

        retry.Should().ContainSingle();
        retry[0].Id.Should().Be(claimed.Id);
        retry[0].AttemptCount.Should().Be(claimed.AttemptCount + 1);
        retry[0].LockToken.Should().NotBe(claimed.LockToken!.Value);
        retry[0].ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task DeadLetterAndReplay_ShouldTransitionStateAndAllowReclaiming()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        OutboxStore store = CreateStore(db);
        await store.Enqueue(
            OutboxMessageTypes.ASSET_BLOB_DELETE,
            new AssetBlobDeletePayload(Guid.NewGuid(), "assets/dead-letter.bin"),
            CancellationToken.None);

        IReadOnlyList<OutboxMessage> batch = await store.ClaimPendingBatch(1, TimeSpan.FromMinutes(5));
        batch.Should().ContainSingle();
        OutboxMessage msg = batch[0];

        // 1. Mark dead-lettered
        var dlSuccess = await store.MarkDeadLettered(msg.Id, msg.LockToken!.Value, "Max attempts reached");
        dlSuccess.Should().BeTrue();

        // Verify state in DB
        await using ApplicationDbContext verifyDb = fixture.CreateDbContext();
        OutboxMessage row = await verifyDb.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == msg.Id);
        row.Status.Should().Be(OutboxMessageStatus.DEAD_LETTERED);
        row.DeadLetteredAt.Should().NotBeNull();
        row.DeadLetterReason.Should().Be("Max attempts reached");
        row.ReplayCount.Should().Be(0);

        // 2. Query GetDeadLetters
        Domain.Core.Dto.Paging.PagedResult<DeadLetterOutboxListItemDto> paged = await store.GetDeadLetters(new GetDeadLettersRequest(1, 10));
        paged.TotalCount.Should().Be(1);
        paged.Items.Should().ContainSingle();
        paged.Items[0].Id.Should().Be(msg.Id);
        paged.Items[0].DeadLetterReason.Should().Be("Max attempts reached");

        // Dead-lettered message must not be claimable
        IReadOnlyList<OutboxMessage> emptyBatch = await store.ClaimPendingBatch(10, TimeSpan.FromMinutes(5));
        emptyBatch.Should().BeEmpty();

        // 3. Replay non-existent -> NOT_FOUND
        (OutboxReplayOutcome notFoundOutcome, ReplayDeadLetterResponseDto? _) = await store.ReplayDeadLetter(Guid.NewGuid());
        notFoundOutcome.Should().Be(OutboxReplayOutcome.NOT_FOUND);

        // 4. Replay valid dead-letter -> SUCCESS
        (OutboxReplayOutcome successOutcome, ReplayDeadLetterResponseDto? replayResponse) = await store.ReplayDeadLetter(msg.Id);
        successOutcome.Should().Be(OutboxReplayOutcome.SUCCESS);
        replayResponse.Should().NotBeNull();
        replayResponse.Id.Should().Be(msg.Id);
        replayResponse.ReplayCount.Should().Be(1);

        // 5. Replay again -> NOT_DEAD_LETTERED (because it is now PENDING)
        (OutboxReplayOutcome conflictOutcome, ReplayDeadLetterResponseDto? _) = await store.ReplayDeadLetter(msg.Id);
        conflictOutcome.Should().Be(OutboxReplayOutcome.NOT_DEAD_LETTERED);

        // 6. Claim batch should now claim the replayed message
        IReadOnlyList<OutboxMessage> replayedBatch = await store.ClaimPendingBatch(10, TimeSpan.FromMinutes(5));
        replayedBatch.Should().ContainSingle();
        replayedBatch[0].Id.Should().Be(msg.Id);
        replayedBatch[0].AttemptCount.Should().Be(1); // 0 incremented on claim to 1
        replayedBatch[0].Status.Should().Be(OutboxMessageStatus.PENDING);
    }

    [Fact]
    public async Task ReplayDeadLetter_WhenAuditFailsInsideTransaction_ShouldRollbackReplayAndKeepDeadLetterState()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        OutboxStore store = CreateStore(db);
        await store.Enqueue(
            OutboxMessageTypes.ASSET_BLOB_DELETE,
            new AssetBlobDeletePayload(Guid.NewGuid(), "assets/rollback-replay.bin"),
            CancellationToken.None);

        IReadOnlyList<OutboxMessage> batch = await store.ClaimPendingBatch(1, TimeSpan.FromMinutes(5));
        batch.Should().ContainSingle();
        OutboxMessage msg = batch[0];

        var dlSuccess = await store.MarkDeadLettered(msg.Id, msg.LockToken!.Value, "Permanent schema corruption");
        dlSuccess.Should().BeTrue();

        await using (ApplicationDbContext verifyInitial = fixture.CreateDbContext())
        {
            OutboxMessage initialRow = await verifyInitial.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == msg.Id);
            initialRow.Status.Should().Be(OutboxMessageStatus.DEAD_LETTERED);
            initialRow.ReplayCount.Should().Be(0);
            initialRow.LastReplayedAt.Should().BeNull();
        }

        // Setup Handler with real EfUnitOfWork, real OutboxStore, and failing IAuditWriter
        var unitOfWork = new EfUnitOfWork(db);
        IAuditWriter auditWriterMock = Substitute.For<IAuditWriter>();
        auditWriterMock.Write(Arg.Any<Domain.Core.Dto.Audit.AuditEvent>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Audit database down."));

        var handler = new Application.UseCases.Admin.Outbox.ReplayDeadLetter.ReplayDeadLetterCommandHandler(
            store,
            unitOfWork,
            auditWriterMock);

        Func<Task<Result<ReplayDeadLetterResponseDto>>> act = () => handler.Handle(
            new Application.UseCases.Admin.Outbox.ReplayDeadLetter.ReplayDeadLetterCommand(msg.Id),
            CancellationToken.None);

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Audit database down.");

        // Verify that database state remained DEAD_LETTERED, ReplayCount is still 0, and no audit row committed
        await using ApplicationDbContext verifyAfterRollback = fixture.CreateDbContext();
        OutboxMessage reloaded = await verifyAfterRollback.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == msg.Id);
        reloaded.Status.Should().Be(OutboxMessageStatus.DEAD_LETTERED);
        reloaded.ReplayCount.Should().Be(0);
        reloaded.LastReplayedAt.Should().BeNull();
        reloaded.DeadLetterReason.Should().Be("Permanent schema corruption");

        (await verifyAfterRollback.AuditLogs.CountAsync()).Should().Be(0);

        IReadOnlyList<OutboxMessage> emptyClaimBatch = await CreateStore(verifyAfterRollback).ClaimPendingBatch(10, TimeSpan.FromMinutes(5));
        emptyClaimBatch.Should().BeEmpty();
    }
}
