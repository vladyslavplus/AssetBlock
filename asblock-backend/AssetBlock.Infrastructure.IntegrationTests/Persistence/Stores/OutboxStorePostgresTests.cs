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
using Npgsql;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class OutboxStorePostgresTests(PostgresFixture fixture)
{
    private const string PRE_OUTBOX_MIGRATION = "20260511061023_AddAssetSoftDelete";

    private static OutboxStore CreateStore(ApplicationDbContext db) =>
        new(db, NullLogger<OutboxStore>.Instance);

    [Fact]
    public async Task ClaimPendingBatch_WhenTwoContextsClaimConcurrently_ShouldNotOverlap()
    {
        await using var seedDb = await fixture.CreateCleanDbContext();
        var seedStore = CreateStore(seedDb);
        for (var i = 0; i < 20; i++)
        {
            await seedStore.Enqueue(OutboxMessageTypes.PURCHASE_COMPLETED, new { i }, CancellationToken.None);
        }

        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var storeA = CreateStore(dbA);
        var storeB = CreateStore(dbB);

        var claimATask = storeA.ClaimPendingBatch(10, TimeSpan.FromMinutes(5));
        var claimBTask = storeB.ClaimPendingBatch(10, TimeSpan.FromMinutes(5));
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
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var unitOfWork = new EfUnitOfWork(db);
        var outbox = CreateStore(db);
        var assetId = asset.Id;
        var originalTitle = asset.Title;

        var act = async () => await unitOfWork.ExecuteInTransaction(async ct =>
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

        await using var verify = fixture.CreateDbContext();
        (await verify.OutboxMessages.CountAsync()).Should().Be(0);
        var reloaded = await verify.Assets.AsNoTracking().SingleAsync(a => a.Id == assetId);
        reloaded.Title.Should().Be(originalTitle);
    }

    [Fact]
    public async Task ExecuteInTransaction_WhenEmailDispatchEnqueuedAndThrows_ShouldRollBackPurchaseAndEmailRow()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("buyer-email-tx", "buyer-email-tx@example.com");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var unitOfWork = new EfUnitOfWork(db);
        var outbox = CreateStore(db);
        var purchaseId = Guid.NewGuid();

        var act = async () => await unitOfWork.ExecuteInTransaction(async ct =>
        {
            db.Purchases.Add(new Purchase
            {
                Id = purchaseId,
                UserId = buyer.Id,
                AssetId = asset.Id,
                StripePaymentId = "cs_email_rollback",
                PurchasedAt = DateTimeOffset.UtcNow
            });
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

        await using var verify = fixture.CreateDbContext();
        (await verify.Purchases.CountAsync(p => p.Id == purchaseId)).Should().Be(0);
        (await verify.OutboxMessages.CountAsync(m => m.Type == OutboxMessageTypes.EMAIL_DISPATCH)).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteInTransaction_WhenEmailDispatchCommits_ShouldPersistSafePayloadWithoutSecrets()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("buyer-email-ok", "buyer-email-ok@example.com");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var unitOfWork = new EfUnitOfWork(db);
        var outbox = CreateStore(db);
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
            db.Purchases.Add(new Purchase
            {
                Id = purchaseId,
                UserId = buyer.Id,
                AssetId = asset.Id,
                StripePaymentId = "cs_email_commit",
                PurchasedAt = DateTimeOffset.UtcNow
            });
            await db.SaveChangesAsync(ct);
            await outbox.Enqueue(OutboxMessageTypes.EMAIL_DISPATCH, payload, ct);
        });

        await using var verify = fixture.CreateDbContext();
        (await verify.Purchases.CountAsync(p => p.Id == purchaseId)).Should().Be(1);
        var row = await verify.OutboxMessages.AsNoTracking()
            .SingleAsync(m => m.Type == OutboxMessageTypes.EMAIL_DISPATCH);
        row.Payload.Should().Contain("\"templateKind\":\"PURCHASE_RECEIPT\"");
        row.Payload.Should().NotContain("\"templateKind\":0");
        row.Payload.Should().Contain("http://localhost:3000/library");
        row.Payload.Should().Contain(buyer.Email);
        row.Payload.Should().NotContain("sk_live");
        row.Payload.Should().NotContain("whsec_");
        row.Payload.Should().NotContain(asset.StorageKey);
        row.Payload.Should().NotContain("Password");
        row.Payload.Should().NotContain("cs_email_commit");
    }

    [Fact]
    public async Task ClaimAndMark_WhenLeaseExpires_StaleWorkerCannotMutateNewLease()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var store = CreateStore(db);
        await store.Enqueue(
            OutboxMessageTypes.ASSET_BLOB_DELETE,
            new AssetBlobDeletePayload(Guid.NewGuid(), "key"),
            CancellationToken.None);

        var first = await store.ClaimPendingBatch(1, TimeSpan.FromMilliseconds(50));
        first.Should().HaveCount(1);
        var stale = first[0];
        stale.LockToken.Should().NotBeNull();

        await Task.Delay(80);

        await using var db2 = fixture.CreateDbContext();
        var store2 = CreateStore(db2);
        var second = await store2.ClaimPendingBatch(1, TimeSpan.FromMinutes(5));
        second.Should().HaveCount(1);
        var fresh = second[0];
        fresh.Id.Should().Be(stale.Id);
        fresh.LockToken.Should().HaveValue();
        fresh.LockToken!.Value.Should().NotBe(stale.LockToken!.Value);

        (await store.MarkProcessed(stale.Id, stale.LockToken!.Value)).Should().BeFalse();
        (await store2.MarkProcessed(fresh.Id, fresh.LockToken!.Value)).Should().BeTrue();

        var row = await db2.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == fresh.Id);
        row.ProcessedAt.Should().NotBeNull();
        row.LockToken.Should().BeNull();
    }

    [Fact]
    public async Task MarkFailed_WhenRetryIsDue_ShouldMakeSameMessageClaimableAgain()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var store = CreateStore(db);
        await store.Enqueue(
            OutboxMessageTypes.ASSET_BLOB_DELETE,
            new AssetBlobDeletePayload(Guid.NewGuid(), "assets/retry-test.bin"),
            CancellationToken.None);

        var first = await store.ClaimPendingBatch(1, TimeSpan.FromMinutes(5));
        first.Should().ContainSingle();
        var claimed = first[0];
        (await store.MarkFailed(
            claimed.Id,
            claimed.LockToken!.Value,
            "transient failure",
            DateTimeOffset.UtcNow.AddMilliseconds(-1))).Should().BeTrue();

        var retry = await store.ClaimPendingBatch(1, TimeSpan.FromMinutes(5));

        retry.Should().ContainSingle();
        retry[0].Id.Should().Be(claimed.Id);
        retry[0].AttemptCount.Should().Be(claimed.AttemptCount + 1);
        retry[0].LockToken.Should().NotBe(claimed.LockToken!.Value);
        retry[0].ProcessedAt.Should().BeNull();
    }

    [Fact]
    public async Task Migrate_WhenLegacyNullStripePaymentId_ShouldBackfillAndSucceed()
    {
        NpgsqlConnection.ClearAllPools();
        await using (var setup = fixture.CreateDbContext())
        {
            await setup.Database.ExecuteSqlRawAsync(
                """
                DROP SCHEMA IF EXISTS public CASCADE;
                CREATE SCHEMA public;
                """);
            await setup.Database.MigrateAsync(PRE_OUTBOX_MIGRATION);
        }

        NpgsqlConnection.ClearAllPools();
        Guid purchaseId;
        await using (var db = fixture.CreateDbContext())
        {
            (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
            var buyer = TestData.CreateUser("legacy-buyer", "legacy-buyer@example.test");
            db.Users.Add(buyer);
            await db.SaveChangesAsync();

            // Insert via SQL: current EF model includes generated search_vector, but this
            // database is still on a pre-FTS migration until MigrateAsync below.
            var assetId = Guid.NewGuid();
            var now = DateTimeOffset.UtcNow;
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO assets ("Id", "AuthorId", "CategoryId", "Title", "Description", "Price", "StorageKey", "FileName", "CreatedAt")
                VALUES ({assetId}, {author.Id}, {category.Id}, {"Legacy Asset"}, {null}, {9.99m}, {"assets/legacy.bin"}, {"package.zip"}, {now});
                """);

            purchaseId = Guid.NewGuid();
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO purchases ("Id", "UserId", "AssetId", "StripePaymentId", "PurchasedAt", "CreatedAt")
                VALUES ({purchaseId}, {buyer.Id}, {assetId}, NULL, {DateTimeOffset.UtcNow}, {DateTimeOffset.UtcNow});
                """);
        }

        NpgsqlConnection.ClearAllPools();
        await using (var migrateDb = fixture.CreateDbContext())
        {
            await migrateDb.Database.MigrateAsync();
        }

        await using var verify = fixture.CreateDbContext();
        var purchase = await verify.Purchases.AsNoTracking().SingleAsync(p => p.Id == purchaseId);
        purchase.StripePaymentId.Should().Be($"legacy-{purchaseId}");
        (await verify.OutboxMessages.CountAsync()).Should().Be(0);
        (await verify.Database.GetPendingMigrationsAsync()).Should().BeEmpty();
    }
}
