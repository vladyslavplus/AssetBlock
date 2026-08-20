using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class OrderStorePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task CreateWithLinesAndPurchases_WhenBundleOrder_ShouldPersistLinesSummingToAmountPaid()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("order-buyer", "order-buyer@example.test");
        db.Users.Add(buyer);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "A", price: 10m);
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "B", price: 30m);
        db.Assets.AddRange(assetA, assetB);
        await db.SaveChangesAsync();
        var versionA = TestData.CreateAssetVersion(assetA.Id);
        var versionB = TestData.CreateAssetVersion(assetB.Id);
        db.AssetVersions.AddRange(versionA, versionB);
        await db.SaveChangesAsync();

        var bundleStore = new BundleStore(db);
        (Bundle bundle, BundleRevision revision) = await bundleStore.CreateWithRevision(
            author.Id,
            "Order Bundle",
            null,
            20m,
            "usd",
            40m,
            [
                new(assetA.Id, 1, assetA.Title, assetA.Price),
                new(assetB.Id, 2, assetB.Title, assetB.Price)
            ]);

        var now = DateTimeOffset.UtcNow;
        var intentId = Guid.NewGuid();
        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyer.Id,
            BundleId = bundle.Id,
            BundleRevisionId = revision.Id,
            ProductTitle = "Order Bundle",
            AmountTotal = 20m,
            Currency = "usd",
            StripeSessionId = "cs_order_alloc",
            Status = CheckoutIntentStatus.COMPLETED,
            CreatedAt = now,
            ExpiresAt = now.AddHours(1),
            CompletedAt = now
        });
        db.CheckoutIntentItems.AddRange(
            BuildIntentItem(intentId, assetA.Id, versionA.Id, author.Id, "A", 10m, 5m, 1),
            BuildIntentItem(intentId, assetB.Id, versionB.Id, author.Id, "B", 30m, 15m, 2));
        await db.SaveChangesAsync();

        var orderId = Guid.NewGuid();
        var lineAId = Guid.NewGuid();
        var lineBId = Guid.NewGuid();
        var order = new Order
        {
            Id = orderId,
            UserId = buyer.Id,
            CheckoutIntentId = intentId,
            BundleId = bundle.Id,
            BundleRevisionId = revision.Id,
            ProductTitle = "Order Bundle",
            StripeSessionId = "cs_order_alloc",
            AmountPaid = 20m,
            Currency = "usd",
            PurchasedAt = now,
            CreatedAt = now
        };
        var lines = new List<OrderLine>
        {
            BuildOrderLine(lineAId, orderId, assetA.Id, versionA.Id, author.Id, "A", 10m, 5m, 1),
            BuildOrderLine(lineBId, orderId, assetB.Id, versionB.Id, author.Id, "B", 30m, 15m, 2)
        };
        var purchases = new List<Purchase>
        {
            TestData.CreatePurchase(buyer.Id, assetA.Id, versionA.Id, purchasedAt: now, orderLineId: lineAId),
            TestData.CreatePurchase(buyer.Id, assetB.Id, versionB.Id, purchasedAt: now, orderLineId: lineBId)
        };

        await new OrderStore(db).CreateWithLinesAndPurchases(order, lines, purchases);

        await using var verify = fixture.CreateDbContext();
        var savedOrder = await verify.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
        var savedLines = await verify.OrderLines.AsNoTracking().Where(l => l.OrderId == orderId).ToListAsync();
        savedLines.Sum(l => l.PricePaid).Should().Be(savedOrder.AmountPaid);
        (await verify.Purchases.CountAsync(p => p.UserId == buyer.Id)).Should().Be(2);
    }

    [Fact]
    public async Task CreateWithLinesAndPurchases_WhenDuplicateStripeSession_ShouldThrowAndLeaveNoPartialRows()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("dup-order-buyer", "dup-order-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Solo", price: 9.99m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var first = TestData.CreatePurchase(buyer.Id, asset.Id, version.Id);
        TestData.AddCompletedPurchase(db, first, asset.Title, author.Id, stripeSessionId: "cs_dup_session");
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var intentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();
        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyer.Id,
            AssetId = asset.Id,
            ProductTitle = asset.Title,
            AmountTotal = 9.99m,
            Currency = "usd",
            Status = CheckoutIntentStatus.COMPLETED,
            CreatedAt = now,
            ExpiresAt = now.AddHours(1),
            CompletedAt = now,
            StripeSessionId = "cs_dup_session_other"
        });
        db.CheckoutIntentItems.Add(BuildIntentItem(intentId, asset.Id, version.Id, author.Id, asset.Title, 9.99m, 9.99m, 1));
        await db.SaveChangesAsync();
        db.ChangeTracker.Clear();

        // Same Stripe session as the first completed order — unique index must reject atomically.
        var conflictOrder = new Order
        {
            Id = orderId,
            UserId = buyer.Id,
            CheckoutIntentId = intentId,
            AssetId = asset.Id,
            ProductTitle = asset.Title,
            StripeSessionId = "cs_dup_session",
            AmountPaid = 9.99m,
            Currency = "usd",
            PurchasedAt = now,
            CreatedAt = now
        };
        var conflictLine = BuildOrderLine(lineId, orderId, asset.Id, version.Id, author.Id, asset.Title, 9.99m, 9.99m, 1);
        // Different buyer so the failure is the Stripe session unique index (not user/asset purchase).
        var otherBuyer = TestData.CreateUser("dup-order-buyer-2", "dup-order-buyer-2@example.test");
        db.Users.Add(otherBuyer);
        await db.SaveChangesAsync();
        conflictOrder.UserId = otherBuyer.Id;
        var conflictPurchase = TestData.CreatePurchase(otherBuyer.Id, asset.Id, version.Id, orderLineId: lineId);

        var act = () => new OrderStore(db).CreateWithLinesAndPurchases(
            conflictOrder,
            [conflictLine],
            [conflictPurchase]);

        await act.Should().ThrowAsync<DuplicateOrderException>();

        await using var verify = fixture.CreateDbContext();
        (await verify.Orders.CountAsync(o => o.StripeSessionId == "cs_dup_session")).Should().Be(1);
        (await verify.OrderLines.CountAsync(l => l.Id == lineId)).Should().Be(0);
        (await verify.Purchases.CountAsync(p => p.OrderLineId == lineId)).Should().Be(0);
    }

    [Fact]
    public async Task ExecuteInTransaction_WhenOrderCreateThrows_ShouldRollBackOrderLinesAndPurchases()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("tx-order-buyer", "tx-order-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Tx", price: 7m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var now = DateTimeOffset.UtcNow;
        var intentId = Guid.NewGuid();
        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyer.Id,
            AssetId = asset.Id,
            ProductTitle = asset.Title,
            AmountTotal = 7m,
            Currency = "usd",
            Status = CheckoutIntentStatus.COMPLETED,
            CreatedAt = now,
            ExpiresAt = now.AddHours(1),
            CompletedAt = now,
            StripeSessionId = "cs_tx_order"
        });
        db.CheckoutIntentItems.Add(BuildIntentItem(intentId, asset.Id, version.Id, author.Id, asset.Title, 7m, 7m, 1));
        await db.SaveChangesAsync();

        var unitOfWork = new EfUnitOfWork(db);
        var orderId = Guid.NewGuid();
        var lineId = Guid.NewGuid();

        var act = async () => await unitOfWork.ExecuteInTransaction(async ct =>
        {
            await new OrderStore(db).CreateWithLinesAndPurchases(
                new Order
                {
                    Id = orderId,
                    UserId = buyer.Id,
                    CheckoutIntentId = intentId,
                    AssetId = asset.Id,
                    ProductTitle = asset.Title,
                    StripeSessionId = "cs_tx_order",
                    AmountPaid = 7m,
                    Currency = "usd",
                    PurchasedAt = now,
                    CreatedAt = now
                },
                [BuildOrderLine(lineId, orderId, asset.Id, version.Id, author.Id, asset.Title, 7m, 7m, 1)],
                [TestData.CreatePurchase(buyer.Id, asset.Id, version.Id, purchasedAt: now, orderLineId: lineId)],
                ct);
            throw new InvalidOperationException("force order rollback");
        });

        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var verify = fixture.CreateDbContext();
        (await verify.Orders.CountAsync(o => o.Id == orderId)).Should().Be(0);
        (await verify.OrderLines.CountAsync(l => l.Id == lineId)).Should().Be(0);
        (await verify.Purchases.CountAsync(p => p.OrderLineId == lineId)).Should().Be(0);
    }

    private static CheckoutIntentItem BuildIntentItem(
        Guid intentId,
        Guid assetId,
        Guid assetVersionId,
        Guid sellerId,
        string title,
        decimal listPrice,
        decimal allocated,
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
            ListPrice = listPrice,
            AllocatedPrice = allocated,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal use",
            LicenseTerms = "terms"
        };

    private static OrderLine BuildOrderLine(
        Guid id,
        Guid orderId,
        Guid assetId,
        Guid assetVersionId,
        Guid sellerId,
        string title,
        decimal listPrice,
        decimal pricePaid,
        int position) =>
        new()
        {
            Id = id,
            OrderId = orderId,
            AssetId = assetId,
            AssetVersionId = assetVersionId,
            SellerId = sellerId,
            Position = position,
            AssetTitleSnapshot = title,
            VersionNumber = 1,
            ListPrice = listPrice,
            PricePaid = pricePaid,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal use",
            LicenseTerms = "terms"
        };
}
