using AssetBlock.Application.UseCases.SellerAnalytics;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class SellerAnalyticsEngagementPostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly _fromDay = new(2024, 7, 1);
    private static readonly DateOnly _toDay = new(2024, 7, 11);

    [Fact]
    public async Task GetOverviewSnapshot_WhenOtherSellerHasEvents_ShouldNotIncludeTheirEngagement()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User sellerA = TestData.CreateUser("iso-a", "iso-a@test.local");
        User sellerB = TestData.CreateUser("iso-b", "iso-b@test.local");
        Category category = TestData.CreateCategory("iso-cat", "iso-cat");
        db.Users.AddRange(sellerA, sellerB);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset assetA = TestData.CreateAsset(sellerA.Id, category.Id, "A Asset", 10m);
        Asset assetB = TestData.CreateAsset(sellerB.Id, category.Id, "B Asset", 10m);
        db.Assets.AddRange(assetA, assetB);
        await db.SaveChangesAsync();

        var eventStore = new AnalyticsEventStore(db);
        await eventStore.TryInsert(CreateAssetView(sellerA.Id, assetA.Id, DayStart(_fromDay)));
        await eventStore.TryInsert(CreateAssetView(sellerB.Id, assetB.Id, DayStart(_fromDay)));

        var store = new SellerAnalyticsStore(db);
        DateTimeOffset from = DayStart(_fromDay);
        DateTimeOffset to = DayStart(_toDay);

        SellerAnalyticsOverviewSnapshot snapshotA = await store.GetOverviewSnapshot(
            sellerA.Id, from, to, from.AddDays(-10), from, topN: 5, AnalyticsGranularity.DAY);
        SellerAnalyticsOverviewSnapshot snapshotB = await store.GetOverviewSnapshot(
            sellerB.Id, from, to, from.AddDays(-10), from, topN: 5, AnalyticsGranularity.DAY);

        snapshotA.CurrentEngagement!.ProductViews.Should().Be(1);
        snapshotB.CurrentEngagement!.ProductViews.Should().Be(1);
    }

    [Fact]
    public async Task GetAssetDetail_WhenAssetBelongsToAnotherSeller_ShouldReturnNull()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User sellerA = TestData.CreateUser("detail-a", "detail-a@test.local");
        User sellerB = TestData.CreateUser("detail-b", "detail-b@test.local");
        Category category = TestData.CreateCategory("detail-cat", "detail-cat");
        db.Users.AddRange(sellerA, sellerB);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset asset = TestData.CreateAsset(sellerA.Id, category.Id, "Owned By A", 10m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        DateTimeOffset from = DayStart(_fromDay);
        DateTimeOffset to = DayStart(_toDay);

        AnalyticsAssetDetailSnapshot? detail = await store.GetAssetDetail(sellerB.Id, asset.Id, from, to, AnalyticsGranularity.DAY);
        detail.Should().BeNull();
    }

    [Fact]
    public async Task GetBundleDetail_WhenBundleBelongsToAnotherSeller_ShouldReturnNull()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User sellerA = TestData.CreateUser("bundle-a", "bundle-a@test.local");
        User sellerB = TestData.CreateUser("bundle-b", "bundle-b@test.local");
        Category category = TestData.CreateCategory("bundle-cat", "bundle-cat");
        db.Users.AddRange(sellerA, sellerB);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset asset = TestData.CreateAsset(sellerA.Id, category.Id, "Bundle Asset", 10m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var bundleStore = new BundleStore(db);
        (Bundle? bundle, BundleRevision _) = await bundleStore.CreateWithRevision(
            sellerA.Id,
            "Seller A Bundle",
            null,
            8m,
            "usd",
            10m,
            [new BundleRevisionItemDraft(asset.Id, 1, asset.Title, asset.Price)]);

        var store = new SellerAnalyticsStore(db);
        DateTimeOffset from = DayStart(_fromDay);
        DateTimeOffset to = DayStart(_toDay);

        AnalyticsBundleDetailSnapshot? detail = await store.GetBundleDetail(sellerB.Id, bundle.Id, from, to, AnalyticsGranularity.DAY);
        detail.Should().BeNull();
    }

    [Fact]
    public async Task GetCollectionsPage_WhenAttributedOrderExists_ShouldCountRevenueFromDurableOrderOnly()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User? seller, User? buyer, Collection? collection, Asset? asset, AssetVersion? version) = await SeedSellerCollectionAsset(db);
        DateTimeOffset from = DayStart(_fromDay);
        DateTimeOffset to = DayStart(_toDay);

        AddCollectionAttributedOrder(
            db, buyer.Id, seller.Id, collection.Id, asset, version, from.AddDays(1), completeOrder: true, pricePaid: 25m);
        AddCollectionAttributedOrder(
            db, buyer.Id, seller.Id, collection.Id, asset, version, from.AddDays(2), completeOrder: false, pricePaid: 15m);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        (IReadOnlyList<AnalyticsCollectionItem>? items, var total, DateTimeOffset? _) = await store.GetCollectionsPage(
            seller.Id, from, to, page: 1, pageSize: 20,
            AnalyticsCollectionSort.ATTRIBUTED_REVENUE, AnalyticsSortDirection.DESC);

        total.Should().Be(1);
        items.Should().HaveCount(1);
        items[0].AttributedCheckoutStarts.Should().Be(2);
        items[0].AttributedCompletedOrders.Should().Be(1);
        items[0].AttributedGrossRevenueCents.Should().Be(2500L);
    }

    [Fact]
    public async Task GetCollectionsPage_WhenUnrelatedSellerOrderReferencesCollection_ShouldNotAttributeRevenue()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User sellerA = TestData.CreateUser("coll-a", "coll-a@test.local");
        User sellerB = TestData.CreateUser("coll-b", "coll-b@test.local");
        User buyer = TestData.CreateUser("coll-buyer", "coll-buyer@test.local");
        Category category = TestData.CreateCategory("coll-cat", "coll-cat");
        db.Users.AddRange(sellerA, sellerB, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset assetA = TestData.CreateAsset(sellerA.Id, category.Id, "A Asset", 10m);
        Asset assetB = TestData.CreateAsset(sellerB.Id, category.Id, "B Asset", 20m);
        db.Assets.AddRange(assetA, assetB);
        await db.SaveChangesAsync();

        AssetVersion versionA = TestData.CreateAssetVersion(assetA.Id);
        AssetVersion versionB = TestData.CreateAssetVersion(assetB.Id);
        db.AssetVersions.AddRange(versionA, versionB);
        await db.SaveChangesAsync();

        Collection collection = TestData.CreateCollection(sellerA.Id, "Seller A Collection", CollectionStatus.PUBLISHED);
        collection.UpdatedAt = DateTimeOffset.UtcNow;
        db.Collections.Add(collection);
        db.CollectionItems.Add(TestData.CreateCollectionItem(collection.Id, assetA.Id, 1));
        await db.SaveChangesAsync();

        DateTimeOffset from = DayStart(_fromDay);
        DateTimeOffset to = DayStart(_toDay);

        AddCollectionAttributedOrder(
            db, buyer.Id, sellerB.Id, collection.Id, assetB, versionB, from.AddDays(1),
            completeOrder: true, pricePaid: 20m);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        (IReadOnlyList<AnalyticsCollectionItem>? itemsA, var _, DateTimeOffset? _) = await store.GetCollectionsPage(
            sellerA.Id, from, to, page: 1, pageSize: 20,
            AnalyticsCollectionSort.ATTRIBUTED_REVENUE, AnalyticsSortDirection.DESC);
        (IReadOnlyList<AnalyticsCollectionItem>? itemsB, var _, DateTimeOffset? _) = await store.GetCollectionsPage(
            sellerB.Id, from, to, page: 1, pageSize: 20,
            AnalyticsCollectionSort.ATTRIBUTED_REVENUE, AnalyticsSortDirection.DESC);

        itemsA.Should().HaveCount(1);
        itemsA[0].AttributedCompletedOrders.Should().Be(0);
        itemsA[0].AttributedGrossRevenueCents.Should().Be(0L);

        itemsB.Should().BeEmpty();
    }

    private static async Task<(User Seller, User Buyer, Collection Collection, Asset Asset, AssetVersion Version)>
        SeedSellerCollectionAsset(ApplicationDbContext db)
    {
        User seller = TestData.CreateUser("attr-seller", "attr-seller@test.local");
        User buyer = TestData.CreateUser("attr-buyer", "attr-buyer@test.local");
        Category category = TestData.CreateCategory("attr-cat", "attr-cat");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset asset = TestData.CreateAsset(seller.Id, category.Id, "Collection Asset", 10m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        Collection collection = TestData.CreateCollection(seller.Id, "Featured", CollectionStatus.PUBLISHED);
        collection.UpdatedAt = DateTimeOffset.UtcNow;
        db.Collections.Add(collection);
        db.CollectionItems.Add(TestData.CreateCollectionItem(collection.Id, asset.Id, 1));
        await db.SaveChangesAsync();

        return (seller, buyer, collection, asset, version);
    }

    private static void AddCollectionAttributedOrder(
        ApplicationDbContext db,
        Guid buyerId,
        Guid sellerId,
        Guid collectionId,
        Asset asset,
        AssetVersion version,
        DateTimeOffset createdAt,
        bool completeOrder,
        decimal pricePaid)
    {
        var intentId = Guid.NewGuid();
        var stripeSessionId = $"test-stripe-{Guid.NewGuid():N}";

        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyerId,
            AssetId = asset.Id,
            ProductTitle = asset.Title,
            AmountTotal = pricePaid,
            Currency = "usd",
            StripeSessionId = stripeSessionId,
            Status = completeOrder ? CheckoutIntentStatus.COMPLETED : CheckoutIntentStatus.PENDING,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddHours(1),
            CompletedAt = completeOrder ? createdAt : null,
            AttributionSource = AnalyticsTrafficSource.COLLECTION,
            AttributionCollectionId = collectionId,
            AnalyticsVisitorId = Guid.NewGuid(),
            AnalyticsSessionId = Guid.NewGuid()
        });

        db.CheckoutIntentItems.Add(new CheckoutIntentItem
        {
            Id = Guid.NewGuid(),
            CheckoutIntentId = intentId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            SellerId = sellerId,
            Position = 1,
            AssetTitleSnapshot = asset.Title,
            VersionNumber = 1,
            ListPrice = pricePaid,
            AllocatedPrice = pricePaid,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal",
            LicenseTerms = "terms"
        });

        if (!completeOrder)
        {
            return;
        }

        var orderId = Guid.NewGuid();
        db.Orders.Add(new Order
        {
            Id = orderId,
            UserId = buyerId,
            CheckoutIntentId = intentId,
            AssetId = asset.Id,
            ProductTitle = asset.Title,
            StripeSessionId = stripeSessionId,
            AmountPaid = pricePaid,
            Currency = "usd",
            PurchasedAt = createdAt,
            CreatedAt = createdAt
        });
        db.OrderLines.Add(new OrderLine
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            SellerId = sellerId,
            Position = 1,
            AssetTitleSnapshot = asset.Title,
            VersionNumber = 1,
            ListPrice = pricePaid,
            PricePaid = pricePaid,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal",
            LicenseTerms = "terms"
        });
    }

    [Fact]
    public async Task GetOverviewSnapshot_WhenNoTelemetryButCompletedCheckout_ShouldReturnCommerceFunnel()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User? seller, User? buyer, Collection? collection, Asset? asset, AssetVersion? version) = await SeedSellerCollectionAsset(db);
        DateTimeOffset from = DayStart(_fromDay);
        DateTimeOffset to = DayStart(_toDay);

        AddCollectionAttributedOrder(
            db, buyer.Id, seller.Id, collection.Id, asset, version, from.AddDays(1),
            completeOrder: true, pricePaid: 15m);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        SellerAnalyticsOverviewSnapshot snapshot = await store.GetOverviewSnapshot(
            seller.Id, from, to, from.AddDays(-10), from, topN: 5, AnalyticsGranularity.DAY);

        snapshot.EngagementAvailableFrom.Should().BeNull();
        snapshot.CommerceFunnel.Should().NotBeNull();
        snapshot.CommerceFunnel!.CompletedOrders.Should().Be(1);
        snapshot.CommerceFunnel.CheckoutStarts.Should().Be(1);
        snapshot.TrafficSources.Should().NotBeNull();
    }

    [Fact]
    public async Task GetOverviewSnapshot_WhenBundleIntentHasMultipleItems_ShouldAttributeRevenueOnce()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User seller = TestData.CreateUser("bundle-rev", "bundle-rev@test.local");
        User buyer = TestData.CreateUser("bundle-buyer", "bundle-buyer@test.local");
        Category category = TestData.CreateCategory("bundle-rev-cat", "bundle-rev-cat");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var assets = new List<(Asset Asset, AssetVersion Version)>();
        for (var i = 0; i < 3; i++)
        {
            Asset asset = TestData.CreateAsset(seller.Id, category.Id, $"Bundle Asset {i}", 10m);
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
            AssetVersion version = TestData.CreateAssetVersion(asset.Id);
            db.AssetVersions.Add(version);
            await db.SaveChangesAsync();
            assets.Add((asset, version));
        }

        var bundleStore = new BundleStore(db);
        (Bundle? bundle, BundleRevision? revision) = await bundleStore.CreateWithRevision(
            seller.Id,
            "Three Item Bundle",
            null,
            25m,
            "usd",
            30m,
            assets.Select((a, index) => new BundleRevisionItemDraft(
                a.Asset.Id, index + 1, a.Asset.Title, a.Asset.Price)).ToList());

        DateTimeOffset from = DayStart(_fromDay);
        DateTimeOffset purchasedAt = from.AddDays(1);
        var intentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var stripeSessionId = $"test-stripe-{Guid.NewGuid():N}";

        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyer.Id,
            BundleId = bundle.Id,
            BundleRevisionId = revision.Id,
            ProductTitle = revision.Title,
            AmountTotal = 25m,
            Currency = "usd",
            StripeSessionId = stripeSessionId,
            Status = CheckoutIntentStatus.COMPLETED,
            CreatedAt = purchasedAt,
            ExpiresAt = purchasedAt.AddHours(1),
            CompletedAt = purchasedAt,
            AttributionSource = AnalyticsTrafficSource.BUNDLE_PAGE
        });

        for (var i = 0; i < assets.Count; i++)
        {
            db.CheckoutIntentItems.Add(new CheckoutIntentItem
            {
                Id = Guid.NewGuid(),
                CheckoutIntentId = intentId,
                AssetId = assets[i].Asset.Id,
                AssetVersionId = assets[i].Version.Id,
                SellerId = seller.Id,
                Position = i + 1,
                AssetTitleSnapshot = assets[i].Asset.Title,
                VersionNumber = 1,
                ListPrice = assets[i].Asset.Price,
                AllocatedPrice = 8.33m,
                LicenseCode = AssetLicenseCode.PERSONAL,
                LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Personal",
                LicenseTerms = "terms"
            });
        }

        db.Orders.Add(new Order
        {
            Id = orderId,
            UserId = buyer.Id,
            CheckoutIntentId = intentId,
            BundleId = bundle.Id,
            BundleRevisionId = revision.Id,
            ProductTitle = revision.Title,
            StripeSessionId = stripeSessionId,
            AmountPaid = 25m,
            Currency = "usd",
            PurchasedAt = purchasedAt,
            CreatedAt = purchasedAt
        });

        for (var i = 0; i < assets.Count; i++)
        {
            db.OrderLines.Add(new OrderLine
            {
                Id = Guid.NewGuid(),
                OrderId = orderId,
                AssetId = assets[i].Asset.Id,
                AssetVersionId = assets[i].Version.Id,
                SellerId = seller.Id,
                Position = i + 1,
                AssetTitleSnapshot = assets[i].Asset.Title,
                VersionNumber = 1,
                ListPrice = assets[i].Asset.Price,
                PricePaid = 8.33m,
                LicenseCode = AssetLicenseCode.PERSONAL,
                LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Personal",
                LicenseTerms = "terms"
            });
        }

        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        DateTimeOffset to = DayStart(_toDay);
        SellerAnalyticsOverviewSnapshot snapshot = await store.GetOverviewSnapshot(
            seller.Id, from, to, from.AddDays(-10), from, topN: 5, AnalyticsGranularity.DAY);

        AnalyticsTrafficSourceRaw bundleSource = snapshot.TrafficSources!
            .Single(s => s.Source == AnalyticsTrafficSource.BUNDLE_PAGE);
        bundleSource.AttributedGrossRevenue.Should().Be(24.99m);
        bundleSource.CompletedOrders.Should().Be(1);
    }

    [Fact]
    public async Task GetOverviewSnapshot_WhenSameVisitorViewsTwoDaysInWeek_ShouldCountOneUniqueVisitor()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User seller = TestData.CreateUser("week-uv", "week-uv@test.local");
        Category category = TestData.CreateCategory("week-uv-cat", "week-uv-cat");
        db.Users.Add(seller);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset asset = TestData.CreateAsset(seller.Id, category.Id, "Week UV Asset", 10m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var visitorId = Guid.NewGuid();
        var monday = new DateOnly(2024, 7, 1);
        var wednesday = new DateOnly(2024, 7, 10);
        var eventStore = new AnalyticsEventStore(db);
        await eventStore.TryInsert(CreateAssetView(
            seller.Id, asset.Id, DayStart(monday).AddHours(10), visitorId: visitorId));
        await eventStore.TryInsert(CreateAssetView(
            seller.Id, asset.Id, DayStart(monday.AddDays(1)).AddHours(11), visitorId: visitorId));

        DateTimeOffset from = DayStart(monday);
        DateTimeOffset to = DayStart(wednesday);
        var store = new SellerAnalyticsStore(db);
        SellerAnalyticsOverviewSnapshot snapshot = await store.GetOverviewSnapshot(
            seller.Id, from, to, from.AddDays(-7), from, topN: 5, AnalyticsGranularity.WEEK);

        AnalyticsEngagementDayBucket weekBucket = snapshot.EngagementDaySeries!.Single(b => b.Date == monday);
        weekBucket.UniqueVisitors.Should().Be(1);
    }

    [Fact]
    public async Task GetAssetDetail_WhenDownloadOnlyVisitorExists_ShouldCountViewVisitorsOnly()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User seller = TestData.CreateUser("uv-view", "uv-view@test.local");
        Category category = TestData.CreateCategory("uv-view-cat", "uv-view-cat");
        db.Users.Add(seller);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset asset = TestData.CreateAsset(seller.Id, category.Id, "UV Asset", 10m);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var viewVisitor = Guid.NewGuid();
        var downloadVisitor = Guid.NewGuid();
        DateTimeOffset from = DayStart(_fromDay);
        DateTimeOffset to = DayStart(_toDay);
        var eventStore = new AnalyticsEventStore(db);
        await eventStore.TryInsert(CreateAssetView(
            seller.Id, asset.Id, from, visitorId: viewVisitor));
        await eventStore.TryInsert(new AnalyticsEvent
        {
            Id = Guid.NewGuid(),
            EventType = AnalyticsEventType.DOWNLOAD_REQUESTED,
            OccurredAt = from.AddHours(2),
            SellerId = seller.Id,
            VisitorId = downloadVisitor,
            SessionId = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            Source = AnalyticsTrafficSource.DIRECT_INTERNAL,
            DeviceClass = AnalyticsDeviceClass.DESKTOP
        });

        var store = new SellerAnalyticsStore(db);
        AnalyticsAssetDetailSnapshot? detail = await store.GetAssetDetail(
            seller.Id, asset.Id, from, to, AnalyticsGranularity.DAY);

        detail.Should().NotBeNull();
        detail.UniqueVisitors.Should().Be(1);
    }

    [Fact]
    public async Task GetOverviewSnapshot_WhenWeekHasRollupMondayAndRawTuesday_ShouldSumBothDaysInWeekSeries()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User seller = TestData.CreateUser("week-mix", "week-mix@test.local");
        Category category = TestData.CreateCategory("week-mix-cat", "week-mix-cat");
        db.Users.Add(seller);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset asset = TestData.CreateAsset(seller.Id, category.Id, "Week Mix Asset", 10m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var monday = new DateOnly(2024, 7, 1);
        var tuesday = new DateOnly(2024, 7, 2);
        var eventStore = new AnalyticsEventStore(db);
        for (var i = 0; i < 4; i++)
        {
            await eventStore.TryInsert(CreateAssetView(
                seller.Id, asset.Id, DayStart(monday).AddHours(i + 1)));
        }

        await eventStore.TryAcquireAndRecomputeDaily(
            monday, monday.AddDays(-1), DateTimeOffset.UtcNow, commandTimeoutSeconds: 120);

        for (var i = 0; i < 3; i++)
        {
            await eventStore.TryInsert(CreateAssetView(
                seller.Id, asset.Id, DayStart(tuesday).AddHours(i + 1)));
        }

        DateTimeOffset from = DayStart(monday);
        DateTimeOffset to = DayStart(tuesday.AddDays(1));
        var store = new SellerAnalyticsStore(db);
        SellerAnalyticsOverviewSnapshot snapshot = await store.GetOverviewSnapshot(
            seller.Id, from, to, from.AddDays(-7), from, topN: 5, AnalyticsGranularity.WEEK);

        AnalyticsEngagementDayBucket weekBucket = snapshot.EngagementDaySeries!.Single(b => b.Date == monday);
        weekBucket.ProductViews.Should().Be(7);
    }

    [Fact]
    public async Task GetOverviewSnapshot_WhenMonthHasMixedRollupAndRawDays_ShouldNotLoseOrDuplicateViews()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User seller = TestData.CreateUser("month-mix", "month-mix@test.local");
        Category category = TestData.CreateCategory("month-mix-cat", "month-mix-cat");
        db.Users.Add(seller);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset asset = TestData.CreateAsset(seller.Id, category.Id, "Month Mix Asset", 10m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var rollupDay = new DateOnly(2024, 6, 10);
        var rawDay = new DateOnly(2024, 6, 20);
        var eventStore = new AnalyticsEventStore(db);
        for (var i = 0; i < 10; i++)
        {
            await eventStore.TryInsert(CreateAssetView(
                seller.Id, asset.Id, DayStart(rollupDay).AddHours(i + 1)));
        }

        await eventStore.TryAcquireAndRecomputeDaily(
            rollupDay, rollupDay.AddDays(-1), DateTimeOffset.UtcNow, commandTimeoutSeconds: 120);

        for (var i = 0; i < 5; i++)
        {
            await eventStore.TryInsert(CreateAssetView(
                seller.Id, asset.Id, DayStart(rawDay).AddHours(i + 1)));
        }

        DateTimeOffset from = DayStart(new DateOnly(2024, 6, 1));
        DateTimeOffset to = DayStart(new DateOnly(2024, 7, 1));
        var store = new SellerAnalyticsStore(db);
        SellerAnalyticsOverviewSnapshot snapshot = await store.GetOverviewSnapshot(
            seller.Id, from, to, from.AddMonths(-1), from, topN: 5, AnalyticsGranularity.MONTH);

        AnalyticsEngagementDayBucket monthBucket = snapshot.EngagementDaySeries!.Single(b => b.Date == new DateOnly(2024, 6, 1));
        monthBucket.ProductViews.Should().Be(15);
    }

    [Fact]
    public async Task GetOverviewSnapshot_WhenComparisonBeforeAvailability_ShouldReturnCurrentEngagementWithoutComparison()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User seller = TestData.CreateUser("comp-avail", "comp-avail@test.local");
        Category category = TestData.CreateCategory("comp-avail-cat", "comp-avail-cat");
        db.Users.Add(seller);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset asset = TestData.CreateAsset(seller.Id, category.Id, "Comp Avail Asset", 10m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var firstDay = new DateOnly(2024, 7, 5);
        var eventStore = new AnalyticsEventStore(db);
        await eventStore.TryInsert(CreateAssetView(seller.Id, asset.Id, DayStart(firstDay)));
        await eventStore.TryInsert(CreateAssetView(
            seller.Id, asset.Id, DayStart(firstDay.AddDays(3))));

        DateTimeOffset from = DayStart(firstDay);
        DateTimeOffset to = DayStart(new DateOnly(2024, 7, 11));
        DateTimeOffset comparisonFrom = DayStart(new DateOnly(2024, 6, 28));
        DateTimeOffset comparisonTo = from;

        var store = new SellerAnalyticsStore(db);
        SellerAnalyticsOverviewSnapshot snapshot = await store.GetOverviewSnapshot(
            seller.Id, from, to, comparisonFrom, comparisonTo, topN: 5, AnalyticsGranularity.DAY);

        snapshot.CurrentEngagement.Should().NotBeNull();
        snapshot.CurrentEngagement!.ProductViews.Should().Be(2);
        snapshot.ComparisonEngagement.Should().BeNull();

        AnalyticsEngagementTotals? totals = AnalyticsEngagementMapper.MapEngagementTotals(
            snapshot.CurrentEngagement, snapshot.ComparisonEngagement);
        totals.Should().NotBeNull();
        totals.ProductViews.Current.Should().Be(2);
        totals.ProductViews.Previous.Should().BeNull();
        totals.ProductViews.AbsoluteChange.Should().BeNull();
        totals.ProductViews.PercentageChange.Should().BeNull();
    }

    [Fact]
    public async Task GetOverviewSnapshot_WhenRangeStartsBeforeAvailability_ShouldNullTotalsAndPartialSeries()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User seller = TestData.CreateUser("pre-avail", "pre-avail@test.local");
        Category category = TestData.CreateCategory("pre-avail-cat", "pre-avail-cat");
        db.Users.Add(seller);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset asset = TestData.CreateAsset(seller.Id, category.Id, "Pre Avail Asset", 10m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var availableDay = new DateOnly(2024, 7, 5);
        var rangeFrom = new DateOnly(2024, 7, 1);
        var rangeTo = new DateOnly(2024, 7, 11);
        var eventStore = new AnalyticsEventStore(db);
        await eventStore.TryInsert(CreateAssetView(seller.Id, asset.Id, DayStart(availableDay)));

        var store = new SellerAnalyticsStore(db);
        SellerAnalyticsOverviewSnapshot snapshot = await store.GetOverviewSnapshot(
            seller.Id,
            DayStart(rangeFrom),
            DayStart(rangeTo),
            DayStart(rangeFrom.AddDays(-10)),
            DayStart(rangeFrom),
            topN: 5,
            AnalyticsGranularity.DAY);

        snapshot.CurrentEngagement.Should().BeNull();
        snapshot.EngagementAvailableFrom.Should().NotBeNull();

        IReadOnlyList<AnalyticsSeriesPoint> series = AnalyticsRange.BuildSeries(
            snapshot.DaySeries,
            rangeFrom,
            rangeTo,
            AnalyticsGranularity.DAY,
            snapshot.EngagementAvailableFrom,
            snapshot.EngagementDaySeries);

        series.Single(p => p.BucketStart == DayStart(new DateOnly(2024, 7, 1))).ProductViews.Should().BeNull();
        series.Single(p => p.BucketStart == DayStart(availableDay)).ProductViews.Should().Be(1);
    }

    [Fact]
    public async Task GetCollectionsPage_WhenIncompleteCoverageAndViewsSort_ShouldOrderByAttributedRevenueDesc()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User seller = TestData.CreateUser("cov-sort", "cov-sort@test.local");
        User buyer = TestData.CreateUser("cov-buyer", "cov-buyer@test.local");
        Category category = TestData.CreateCategory("cov-sort-cat", "cov-sort-cat");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset assetHighViews = TestData.CreateAsset(seller.Id, category.Id, "High Views Asset", 10m);
        Asset assetHighRevenue = TestData.CreateAsset(seller.Id, category.Id, "High Revenue Asset", 10m);
        db.Assets.AddRange(assetHighViews, assetHighRevenue);
        await db.SaveChangesAsync();

        AssetVersion versionHighViews = TestData.CreateAssetVersion(assetHighViews.Id);
        AssetVersion versionHighRevenue = TestData.CreateAssetVersion(assetHighRevenue.Id);
        db.AssetVersions.AddRange(versionHighViews, versionHighRevenue);
        await db.SaveChangesAsync();

        Collection highViews = TestData.CreateCollection(seller.Id, "High Views", CollectionStatus.PUBLISHED);
        Collection highRevenue = TestData.CreateCollection(seller.Id, "High Revenue", CollectionStatus.PUBLISHED);
        db.Collections.AddRange(highViews, highRevenue);
        db.CollectionItems.AddRange(
            TestData.CreateCollectionItem(highViews.Id, assetHighViews.Id, 1),
            TestData.CreateCollectionItem(highRevenue.Id, assetHighRevenue.Id, 1));
        await db.SaveChangesAsync();

        DateTimeOffset from = DayStart(_fromDay);
        DateTimeOffset to = DayStart(_toDay);

        // Telemetry starts mid-range → incomplete coverage for [from, to).
        var eventStore = new AnalyticsEventStore(db);
        for (var i = 0; i < 5; i++)
        {
            await eventStore.TryInsert(new AnalyticsEvent
            {
                Id = Guid.NewGuid(),
                EventType = AnalyticsEventType.COLLECTION_VIEW,
                OccurredAt = from.AddDays(5).AddMinutes(i),
                SellerId = seller.Id,
                VisitorId = Guid.NewGuid(),
                SessionId = Guid.NewGuid(),
                CollectionId = highViews.Id,
                Source = AnalyticsTrafficSource.COLLECTION,
                DeviceClass = AnalyticsDeviceClass.DESKTOP
            });
        }

        AddCollectionAttributedOrder(
            db, buyer.Id, seller.Id, highViews.Id, assetHighViews, versionHighViews,
            from.AddDays(6), completeOrder: true, pricePaid: 10m);
        AddCollectionAttributedOrder(
            db, buyer.Id, seller.Id, highRevenue.Id, assetHighRevenue, versionHighRevenue,
            from.AddDays(6), completeOrder: true, pricePaid: 90m);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        (IReadOnlyList<AnalyticsCollectionItem>? items, var _, DateTimeOffset? engagementAvailableFrom) = await store.GetCollectionsPage(
            seller.Id, from, to, page: 1, pageSize: 20,
            AnalyticsCollectionSort.VIEWS, AnalyticsSortDirection.DESC);

        engagementAvailableFrom.Should().NotBeNull();
        from.Should().BeBefore(engagementAvailableFrom!.Value);
        items.Should().HaveCount(2);
        items[0].CollectionId.Should().Be(highRevenue.Id);
        items[1].CollectionId.Should().Be(highViews.Id);
    }

    [Fact]
    public async Task GetCollectionsPage_WhenRecentSortRequested_ShouldOrderByLatestEventNotUpdatedAt()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User seller = TestData.CreateUser("recent-sort", "recent-sort@test.local");
        Category category = TestData.CreateCategory("recent-sort-cat", "recent-sort-cat");
        db.Users.Add(seller);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        Asset assetA = TestData.CreateAsset(seller.Id, category.Id, "Asset A", 10m);
        Asset assetB = TestData.CreateAsset(seller.Id, category.Id, "Asset B", 10m);
        db.Assets.AddRange(assetA, assetB);
        await db.SaveChangesAsync();

        Collection staleUpdated = TestData.CreateCollection(seller.Id, "Stale UpdatedAt", CollectionStatus.PUBLISHED);
        staleUpdated.UpdatedAt = DateTimeOffset.UtcNow;
        Collection freshEvent = TestData.CreateCollection(seller.Id, "Fresh Event", CollectionStatus.PUBLISHED);
        freshEvent.UpdatedAt = DayStart(_fromDay);
        db.Collections.AddRange(staleUpdated, freshEvent);
        db.CollectionItems.AddRange(
            TestData.CreateCollectionItem(staleUpdated.Id, assetA.Id, 1),
            TestData.CreateCollectionItem(freshEvent.Id, assetB.Id, 1));
        await db.SaveChangesAsync();

        DateTimeOffset from = DayStart(_fromDay);
        DateTimeOffset to = DayStart(_toDay);
        var eventStore = new AnalyticsEventStore(db);
        await eventStore.TryInsert(new AnalyticsEvent
        {
            Id = Guid.NewGuid(),
            EventType = AnalyticsEventType.COLLECTION_VIEW,
            OccurredAt = from.AddDays(1),
            SellerId = seller.Id,
            VisitorId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            CollectionId = staleUpdated.Id,
            Source = AnalyticsTrafficSource.COLLECTION,
            DeviceClass = AnalyticsDeviceClass.DESKTOP
        });
        await eventStore.TryInsert(new AnalyticsEvent
        {
            Id = Guid.NewGuid(),
            EventType = AnalyticsEventType.COLLECTION_VIEW,
            OccurredAt = from.AddDays(8),
            SellerId = seller.Id,
            VisitorId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            CollectionId = freshEvent.Id,
            Source = AnalyticsTrafficSource.COLLECTION,
            DeviceClass = AnalyticsDeviceClass.DESKTOP
        });

        var store = new SellerAnalyticsStore(db);
        (IReadOnlyList<AnalyticsCollectionItem>? items, var _, DateTimeOffset? _) = await store.GetCollectionsPage(
            seller.Id, from, to, page: 1, pageSize: 20,
            AnalyticsCollectionSort.RECENT, AnalyticsSortDirection.DESC);

        items.Should().HaveCount(2);
        items[0].CollectionId.Should().Be(freshEvent.Id);
        items[1].CollectionId.Should().Be(staleUpdated.Id);
    }

    private static AnalyticsEvent CreateAssetView(
        Guid sellerId,
        Guid assetId,
        DateTimeOffset occurredAt,
        Guid? visitorId = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            EventType = AnalyticsEventType.ASSET_VIEW,
            OccurredAt = occurredAt,
            SellerId = sellerId,
            VisitorId = visitorId ?? Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            AssetId = assetId,
            Source = AnalyticsTrafficSource.CATALOG,
            DeviceClass = AnalyticsDeviceClass.DESKTOP
        };

    private static DateTimeOffset DayStart(DateOnly day) =>
        new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));
}
