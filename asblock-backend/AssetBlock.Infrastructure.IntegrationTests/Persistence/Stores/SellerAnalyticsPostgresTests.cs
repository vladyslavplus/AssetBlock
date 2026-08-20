using System.Data.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class SellerAnalyticsPostgresTests(PostgresFixture fixture)
{
    private static async Task<(User Seller, User Buyer, Category Category, Asset Asset1, AssetVersion Version1)>
        SeedSellerBuyerAsset(ApplicationDbContext db, string suffix = "")
    {
        var seller = TestData.CreateUser($"seller{suffix}", $"seller{suffix}@test.local");
        var buyer = TestData.CreateUser($"buyer{suffix}", $"buyer{suffix}@test.local");
        var category = TestData.CreateCategory($"cat{suffix}", $"cat{suffix}");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var asset = TestData.CreateAsset(seller.Id, category.Id, title: $"Asset {suffix}", price: 10m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        return (seller, buyer, category, asset, version);
    }

    private static void AddDirectOrder(
        ApplicationDbContext db,
        Guid buyerId,
        Asset asset,
        AssetVersion version,
        Guid sellerId,
        decimal pricePaid,
        DateTimeOffset purchasedAt)
    {
        var intentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var sessionId = $"test-stripe-{Guid.NewGuid():N}";

        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyerId,
            AssetId = asset.Id,
            ProductTitle = asset.Title,
            AmountTotal = pricePaid,
            Currency = "usd",
            StripeSessionId = sessionId,
            Status = CheckoutIntentStatus.COMPLETED,
            CreatedAt = purchasedAt,
            ExpiresAt = purchasedAt.AddHours(1),
            CompletedAt = purchasedAt
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
            LicenseDisplayName = "Personal use",
            LicenseTerms = "terms"
        });
        db.Orders.Add(new Order
        {
            Id = orderId,
            UserId = buyerId,
            CheckoutIntentId = intentId,
            AssetId = asset.Id,
            ProductTitle = asset.Title,
            StripeSessionId = sessionId,
            AmountPaid = pricePaid,
            Currency = "usd",
            PurchasedAt = purchasedAt,
            CreatedAt = purchasedAt
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
            LicenseDisplayName = "Personal use",
            LicenseTerms = "terms"
        });
    }

    private static async Task AddBundleOrder(
        ApplicationDbContext db,
        Guid buyerId,
        Guid sellerId,
        List<(Asset Asset, AssetVersion Version, decimal PricePaid)> assets,
        decimal bundleAmountPaid,
        DateTimeOffset purchasedAt)
    {
        var bundleStore = new BundleStore(db);
        var (bundle, revision) = await bundleStore.CreateWithRevision(
            sellerId,
            $"Bundle-{Guid.NewGuid():N}",
            null,
            bundleAmountPaid,
            "usd",
            assets.Sum(a => a.Asset.Price),
            assets.Select((a, i) => new BundleRevisionItemDraft(
                a.Asset.Id, i + 1, a.Asset.Title, a.Asset.Price)).ToList());

        var intentId = Guid.NewGuid();
        var orderId = Guid.NewGuid();
        var sessionId = $"test-stripe-{Guid.NewGuid():N}";

        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyerId,
            BundleId = bundle.Id,
            BundleRevisionId = revision.Id,
            ProductTitle = revision.Title,
            AmountTotal = bundleAmountPaid,
            Currency = "usd",
            StripeSessionId = sessionId,
            Status = CheckoutIntentStatus.COMPLETED,
            CreatedAt = purchasedAt,
            ExpiresAt = purchasedAt.AddHours(1),
            CompletedAt = purchasedAt
        });

        for (var i = 0; i < assets.Count; i++)
        {
            db.CheckoutIntentItems.Add(new CheckoutIntentItem
            {
                Id = Guid.NewGuid(),
                CheckoutIntentId = intentId,
                AssetId = assets[i].Asset.Id,
                AssetVersionId = assets[i].Version.Id,
                SellerId = sellerId,
                Position = i + 1,
                AssetTitleSnapshot = assets[i].Asset.Title,
                VersionNumber = 1,
                ListPrice = assets[i].Asset.Price,
                AllocatedPrice = assets[i].PricePaid,
                LicenseCode = AssetLicenseCode.PERSONAL,
                LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Personal use",
                LicenseTerms = "terms"
            });
        }

        db.Orders.Add(new Order
        {
            Id = orderId,
            UserId = buyerId,
            CheckoutIntentId = intentId,
            BundleId = bundle.Id,
            BundleRevisionId = revision.Id,
            ProductTitle = revision.Title,
            StripeSessionId = sessionId,
            AmountPaid = bundleAmountPaid,
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
                SellerId = sellerId,
                Position = i + 1,
                AssetTitleSnapshot = assets[i].Asset.Title,
                VersionNumber = 1,
                ListPrice = assets[i].Asset.Price,
                PricePaid = assets[i].PricePaid,
                LicenseCode = AssetLicenseCode.PERSONAL,
                LicenseTemplateVersion = "1.0",
                LicenseDisplayName = "Personal use",
                LicenseTerms = "terms"
            });
        }

        await db.SaveChangesAsync();
    }

    private static async Task<SellerAnalyticsOverviewSnapshot> GetSnapshot(
        SellerAnalyticsStore store,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to)
    {
        var compFrom = from.AddDays(-(to - from).TotalDays);
        return await store.GetOverviewSnapshot(
            sellerId, from, to, compFrom, from, 5, AnalyticsGranularity.DAY);
    }


    [Fact]
    public async Task GetOverviewSnapshot_EmptySeller_ReturnsAllZeros()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, _, _, _, _) = await SeedSellerBuyerAsset(db, "empty");

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 1, 11, 0, 0, 0, TimeSpan.Zero);

        var snapshot = await GetSnapshot(store, seller.Id, from, to);

        snapshot.CurrentFacts.GrossRevenue.Should().Be(0);
        snapshot.CurrentFacts.Orders.Should().Be(0);
        snapshot.CurrentFacts.Units.Should().Be(0);
    }


    [Fact]
    public async Task GetOverviewSnapshot_SingleDirectOrder_ReturnsCorrectRevenue()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, version) = await SeedSellerBuyerAsset(db, "direct");

        var purchasedAt = new DateTimeOffset(2024, 3, 15, 10, 0, 0, TimeSpan.Zero);
        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 9.99m, purchasedAt);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 4, 1, 0, 0, 0, TimeSpan.Zero);

        var snapshot = await GetSnapshot(store, seller.Id, from, to);
        var facts = snapshot.CurrentFacts;

        facts.GrossRevenue.Should().Be(9.99m);
        facts.Orders.Should().Be(1);
        facts.Units.Should().Be(1);
        facts.DirectRevenue.Should().Be(9.99m);
        facts.BundleRevenue.Should().Be(0);
    }


    [Fact]
    public async Task GetOverviewSnapshot_BundleOrder2Assets_Counts1Order2Units()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var seller = TestData.CreateUser("bseller", "bseller@test.local");
        var buyer = TestData.CreateUser("bbuyer", "bbuyer@test.local");
        var category = TestData.CreateCategory("bcat", "bcat");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var a1 = TestData.CreateAsset(seller.Id, category.Id, "BundleA1", 5m);
        var a2 = TestData.CreateAsset(seller.Id, category.Id, "BundleA2", 5m);
        db.Assets.AddRange(a1, a2);
        await db.SaveChangesAsync();

        var v1 = TestData.CreateAssetVersion(a1.Id);
        var v2 = TestData.CreateAssetVersion(a2.Id);
        db.AssetVersions.AddRange(v1, v2);
        await db.SaveChangesAsync();

        var purchasedAt = new DateTimeOffset(2024, 5, 10, 0, 0, 0, TimeSpan.Zero);
        await AddBundleOrder(db, buyer.Id, seller.Id,
            [(a1, v1, 4m), (a2, v2, 4m)],
            bundleAmountPaid: 8m,
            purchasedAt);

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);

        var facts = (await GetSnapshot(store, seller.Id, from, to)).CurrentFacts;

        facts.Orders.Should().Be(1);
        facts.Units.Should().Be(2);
        facts.BundleRevenue.Should().Be(8m);
        facts.DirectRevenue.Should().Be(0);
    }


    [Fact]
    public async Task GetOverviewSnapshot_OtherSellerOrder_NotCounted()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (sellerA, buyer, _, assetA, versionA) = await SeedSellerBuyerAsset(db, "isol-a");
        var (sellerB, _, _, assetB, versionB) = await SeedSellerBuyerAsset(db, "isol-b");

        var purchasedAt = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        AddDirectOrder(db, buyer.Id, assetA, versionA, sellerA.Id, 15m, purchasedAt);
        AddDirectOrder(db, buyer.Id, assetB, versionB, sellerB.Id, 25m, purchasedAt.AddHours(1));
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 3, 1, 0, 0, 0, TimeSpan.Zero);

        var factsA = (await GetSnapshot(store, sellerA.Id, from, to)).CurrentFacts;
        var factsB = (await GetSnapshot(store, sellerB.Id, from, to)).CurrentFacts;

        factsA.GrossRevenue.Should().Be(15m);
        factsA.Orders.Should().Be(1);
        factsB.GrossRevenue.Should().Be(25m);
        factsB.Orders.Should().Be(1);
    }


    [Fact]
    public async Task GetOverviewSnapshot_CustomerMetrics_ComputedCorrectly()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, _, _, asset, version) = await SeedSellerBuyerAsset(db, "cust");

        var buyer1 = TestData.CreateUser("buyer1-c", "buyer1@cust.test");
        var buyer2 = TestData.CreateUser("buyer2-c", "buyer2@cust.test");
        var buyer3 = TestData.CreateUser("buyer3-c", "buyer3@cust.test");
        db.Users.AddRange(buyer1, buyer2, buyer3);
        await db.SaveChangesAsync();

        var periodStart = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var beforePeriod = periodStart.AddDays(-10);

        AddDirectOrder(db, buyer1.Id, asset, version, seller.Id, 10m, beforePeriod);
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer1.Id, asset, version, seller.Id, 10m, periodStart.AddDays(1));
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer2.Id, asset, version, seller.Id, 10m, periodStart.AddDays(2));
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer2.Id, asset, version, seller.Id, 10m, periodStart.AddDays(3));
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer3.Id, asset, version, seller.Id, 10m, periodStart.AddDays(4));
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var facts = (await GetSnapshot(store, seller.Id, periodStart, periodStart.AddDays(30))).CurrentFacts;

        facts.UniqueCustomers.Should().Be(3);
        facts.NewCustomers.Should().Be(2);
        facts.RepeatCustomers.Should().Be(1);
    }


    [Fact]
    public async Task GetOverviewSnapshot_DaySeries_OrdersGroupedByUtcDay()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, version) = await SeedSellerBuyerAsset(db, "series");

        var day1Start = new DateTimeOffset(2024, 4, 10, 5, 0, 0, TimeSpan.Zero);
        var day1End = new DateTimeOffset(2024, 4, 10, 23, 59, 59, TimeSpan.Zero);
        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 10m, day1Start);
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 20m, day1End);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 4, 10, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 4, 11, 0, 0, 0, TimeSpan.Zero);

        var series = (await GetSnapshot(store, seller.Id, from, to)).DaySeries;

        series.Should().HaveCount(1);
        series[0].Date.Should().Be(new DateOnly(2024, 4, 10));
        series[0].GrossRevenue.Should().Be(30m);
        series[0].Orders.Should().Be(2);
    }


    [Fact]
    public async Task GetProductsPage_DeletedAsset_ReturnsHistoricalDataWithUnavailableFlag()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, version) = await SeedSellerBuyerAsset(db, "softdel");

        var purchasedAt = new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero);
        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 15m, purchasedAt);
        await db.SaveChangesAsync();

        asset.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var (items, _) = await store.GetProductsPage(
            seller.Id,
            new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero),
            AnalyticsProductTypeFilter.ASSET,
            1, 20, AnalyticsProductSort.REVENUE,
            AnalyticsSortDirection.DESC);

        items.Should().HaveCount(1);
        items[0].IsDeletedOrArchived.Should().BeTrue();
        items[0].GrossRevenue.Should().Be(15m);
    }


    [Fact]
    public async Task GetProductsPage_ArchivedBundle_ReturnsHistoricalDataWithArchivedFlag()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var seller = TestData.CreateUser("arcseller", "arcseller@test.local");
        var buyer = TestData.CreateUser("arcbuyer", "arcbuyer@test.local");
        var category = TestData.CreateCategory("arccat", "arccat");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var a1 = TestData.CreateAsset(seller.Id, category.Id, "ArcA1", 6m);
        db.Assets.Add(a1);
        await db.SaveChangesAsync();
        var v1 = TestData.CreateAssetVersion(a1.Id);
        db.AssetVersions.Add(v1);
        await db.SaveChangesAsync();

        var purchasedAt = new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero);
        await AddBundleOrder(db, buyer.Id, seller.Id, [(a1, v1, 5m)], 5m, purchasedAt);

        var bundle = await db.Bundles.SingleAsync(b => b.SellerId == seller.Id);
        bundle.ArchivedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var (items, _) = await store.GetProductsPage(
            seller.Id,
            new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero),
            AnalyticsProductTypeFilter.BUNDLE,
            1, 20, AnalyticsProductSort.REVENUE,
            AnalyticsSortDirection.DESC);

        items.Should().HaveCount(1);
        items[0].IsDeletedOrArchived.Should().BeTrue();
        items[0].GrossRevenue.Should().Be(5m);
    }


    [Fact]
    public async Task GetProductsPage_BundleRevenue_IsSumOfOrderAmountPaidAndUnitsSoldIsLineCount()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var seller = TestData.CreateUser("brevseller", "brevseller@test.local");
        var buyer = TestData.CreateUser("brevbuyer", "brevbuyer@test.local");
        var category = TestData.CreateCategory("brevcat", "brevcat");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var a1 = TestData.CreateAsset(seller.Id, category.Id, "BrevA1", 10m);
        var a2 = TestData.CreateAsset(seller.Id, category.Id, "BrevA2", 10m);
        db.Assets.AddRange(a1, a2);
        await db.SaveChangesAsync();
        var v1 = TestData.CreateAssetVersion(a1.Id);
        var v2 = TestData.CreateAssetVersion(a2.Id);
        db.AssetVersions.AddRange(v1, v2);
        await db.SaveChangesAsync();

        var purchasedAt = new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero);
        await AddBundleOrder(db, buyer.Id, seller.Id,
            [(a1, v1, 7.50m), (a2, v2, 7.50m)],
            bundleAmountPaid: 15m,
            purchasedAt);

        var store = new SellerAnalyticsStore(db);
        var (items, _) = await store.GetProductsPage(
            seller.Id,
            new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero),
            AnalyticsProductTypeFilter.BUNDLE,
            1, 20, AnalyticsProductSort.REVENUE,
            AnalyticsSortDirection.DESC);

        items.Should().HaveCount(1);
        items[0].GrossRevenue.Should().Be(15m);
        items[0].Orders.Should().Be(1);
        items[0].UnitsSold.Should().Be(2);
    }


    [Fact]
    public async Task GetProductsPage_NoSales_ReturnsZeroMetricsWithPositiveTotalCount()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, _, _, _, _) = await SeedSellerBuyerAsset(db, "nosales");

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero);

        var (items, total) = await store.GetProductsPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ASSET,
            1, 20, AnalyticsProductSort.REVENUE, AnalyticsSortDirection.DESC);

        total.Should().BeGreaterThan(0);
        items.Should().HaveCount(1);
        items[0].GrossRevenue.Should().Be(0);
        items[0].Orders.Should().Be(0);
        items[0].UnitsSold.Should().Be(0);
    }


    [Fact]
    public async Task GetProductsPage_AllType_GlobalPaginationWorks()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var seller = TestData.CreateUser("pagesel", "pagesel@test.local");
        var buyer = TestData.CreateUser("pagebuy", "pagebuy@test.local");
        var category = TestData.CreateCategory("pagecat", "pagecat");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var assets = new List<(Asset Asset, AssetVersion Version)>();
        for (var i = 0; i < 3; i++)
        {
            var asset = TestData.CreateAsset(seller.Id, category.Id, $"PageAsset{i}", 5m);
            db.Assets.Add(asset);
            await db.SaveChangesAsync();
            var version = TestData.CreateAssetVersion(asset.Id);
            db.AssetVersions.Add(version);
            await db.SaveChangesAsync();
            assets.Add((asset, version));
        }

        var purchasedAt = new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero);
        AddDirectOrder(db, buyer.Id, assets[0].Asset, assets[0].Version, seller.Id, 30m, purchasedAt);
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer.Id, assets[1].Asset, assets[1].Version, seller.Id, 20m, purchasedAt);
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer.Id, assets[2].Asset, assets[2].Version, seller.Id, 10m, purchasedAt);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 11, 1, 0, 0, 0, TimeSpan.Zero);

        var (page1, total) = await store.GetProductsPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ALL,
            1, 2, AnalyticsProductSort.REVENUE, AnalyticsSortDirection.DESC);

        total.Should().Be(3);
        page1.Should().HaveCount(2);
        page1[0].GrossRevenue.Should().Be(30m);
        page1[1].GrossRevenue.Should().Be(20m);

        var (page2, _) = await store.GetProductsPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ALL,
            2, 2, AnalyticsProductSort.REVENUE, AnalyticsSortDirection.DESC);

        page2.Should().HaveCount(1);
        page2[0].GrossRevenue.Should().Be(10m);
    }


    [Fact]
    public async Task GetProductsPage_TieBreak_SortsByProductKindThenId()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var seller = TestData.CreateUser("tiesel", "tiesel@test.local");
        var buyer = TestData.CreateUser("tiebuy", "tiebuy@test.local");
        var category = TestData.CreateCategory("tiecat", "tiecat");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var assetA = TestData.CreateAsset(seller.Id, category.Id, "TieA", 5m);
        var assetB = TestData.CreateAsset(seller.Id, category.Id, "TieB", 5m);
        db.Assets.AddRange(assetA, assetB);
        await db.SaveChangesAsync();
        var vA = TestData.CreateAssetVersion(assetA.Id);
        var vB = TestData.CreateAssetVersion(assetB.Id);
        db.AssetVersions.AddRange(vA, vB);
        await db.SaveChangesAsync();

        var purchasedAt = new DateTimeOffset(2024, 11, 1, 0, 0, 0, TimeSpan.Zero);
        AddDirectOrder(db, buyer.Id, assetA, vA, seller.Id, 10m, purchasedAt);
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer.Id, assetB, vB, seller.Id, 10m, purchasedAt);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero);

        var (items, _) = await store.GetProductsPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ASSET,
            1, 10, AnalyticsProductSort.REVENUE, AnalyticsSortDirection.DESC);

        items.Should().HaveCount(2);
        items[0].ProductId.CompareTo(items[1].ProductId).Should().BeLessThan(0);
    }


    [Fact]
    public async Task GetProductsPage_RecentSort_PutsNullLatestSaleLast()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var seller = TestData.CreateUser("recsel", "recsel@test.local");
        var buyer = TestData.CreateUser("recbuy", "recbuy@test.local");
        var category = TestData.CreateCategory("reccat", "reccat");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var sold = TestData.CreateAsset(seller.Id, category.Id, "Sold", 5m);
        var unsold = TestData.CreateAsset(seller.Id, category.Id, "Unsold", 5m);
        db.Assets.AddRange(sold, unsold);
        await db.SaveChangesAsync();
        var vSold = TestData.CreateAssetVersion(sold.Id);
        var vUnsold = TestData.CreateAssetVersion(unsold.Id);
        db.AssetVersions.AddRange(vSold, vUnsold);
        await db.SaveChangesAsync();

        AddDirectOrder(db, buyer.Id, sold, vSold, seller.Id, 10m,
            new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var (items, _) = await store.GetProductsPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ASSET,
            1, 10, AnalyticsProductSort.RECENT, AnalyticsSortDirection.DESC);

        items.Should().HaveCount(2);
        items[0].ProductId.Should().Be(sold.Id);
        items[1].LatestSaleAt.Should().BeNull();
    }


    [Fact]
    public async Task GetProductsPage_RatingSort_PutsNullRatingsLast()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var seller = TestData.CreateUser("ratsel", "ratsel@test.local");
        var buyer = TestData.CreateUser("ratbuy", "ratbuy@test.local");
        var category = TestData.CreateCategory("ratcat", "ratcat");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var rated = TestData.CreateAsset(seller.Id, category.Id, "Rated", 5m);
        var unrated = TestData.CreateAsset(seller.Id, category.Id, "Unrated", 5m);
        db.Assets.AddRange(rated, unrated);
        await db.SaveChangesAsync();
        var vRated = TestData.CreateAssetVersion(rated.Id);
        var vUnrated = TestData.CreateAssetVersion(unrated.Id);
        db.AssetVersions.AddRange(vRated, vUnrated);
        await db.SaveChangesAsync();

        db.Reviews.Add(TestData.CreateReview(buyer.Id, rated.Id));
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);

        var (ascItems, _) = await store.GetProductsPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ASSET,
            1, 10, AnalyticsProductSort.RATING, AnalyticsSortDirection.ASC);

        ascItems[^1].AverageRating.Should().BeNull();

        var (descItems, _) = await store.GetProductsPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ASSET,
            1, 10, AnalyticsProductSort.RATING, AnalyticsSortDirection.DESC);

        descItems[^1].AverageRating.Should().BeNull();
    }


    [Fact]
    public async Task GetProductsPage_BundlePurchase_CountsAsAllocatedRevenue()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var seller = TestData.CreateUser("splitsel", "splitsel@test.local");
        var buyer = TestData.CreateUser("splitbuy", "splitbuy@test.local");
        var category = TestData.CreateCategory("splitcat", "splitcat");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var a1 = TestData.CreateAsset(seller.Id, category.Id, "SplitA1", 10m);
        db.Assets.Add(a1);
        await db.SaveChangesAsync();
        var v1 = TestData.CreateAssetVersion(a1.Id);
        db.AssetVersions.Add(v1);
        await db.SaveChangesAsync();

        AddDirectOrder(db, buyer.Id, a1, v1, seller.Id, 10m,
            new DateTimeOffset(2024, 11, 1, 0, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();
        await AddBundleOrder(db, buyer.Id, seller.Id, [(a1, v1, 6m)], 6m,
            new DateTimeOffset(2024, 11, 5, 0, 0, 0, TimeSpan.Zero));

        var store = new SellerAnalyticsStore(db);
        var (items, _) = await store.GetProductsPage(
            seller.Id,
            new DateTimeOffset(2024, 11, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero),
            AnalyticsProductTypeFilter.ASSET,
            1, 20, AnalyticsProductSort.REVENUE,
            AnalyticsSortDirection.DESC);

        items.Should().HaveCount(1);
        items[0].GrossRevenue.Should().Be(16m);
        items[0].DirectRevenue.Should().Be(10m);
        items[0].BundleAllocatedRevenue.Should().Be(6m);
    }


    [Fact]
    public async Task GetSalesPage_KeysetPagination_ReturnsCorrectPage()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, version) = await SeedSellerBuyerAsset(db, "keyset");

        var t1 = new DateTimeOffset(2024, 10, 1, 1, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2024, 10, 1, 2, 0, 0, TimeSpan.Zero);
        var t3 = new DateTimeOffset(2024, 10, 1, 3, 0, 0, TimeSpan.Zero);

        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 10m, t1);
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 20m, t2);
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 30m, t3);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 11, 1, 0, 0, 0, TimeSpan.Zero);

        var (page1, hasMore1) = await store.GetSalesPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ALL, null, null, 2);
        hasMore1.Should().BeTrue();
        page1.Should().HaveCount(2);
        page1[0].PurchasedAt.Should().Be(t3);
        page1[1].PurchasedAt.Should().Be(t2);

        var (page2, hasMore2) = await store.GetSalesPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ALL,
            page1[^1].PurchasedAt, page1[^1].OrderId, 2);
        hasMore2.Should().BeFalse();
        page2.Should().HaveCount(1);
        page2[0].PurchasedAt.Should().Be(t1);
    }


    [Fact]
    public async Task GetSalesPage_SamePurchasedAt_UsesOrderIdTieBreak()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, version) = await SeedSellerBuyerAsset(db, "sametime");

        var sameTime = new DateTimeOffset(2024, 10, 5, 12, 0, 0, TimeSpan.Zero);
        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 10m, sameTime);
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 20m, sameTime);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 11, 1, 0, 0, 0, TimeSpan.Zero);

        var (page1, hasMore1) = await store.GetSalesPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ALL, null, null, 1);
        hasMore1.Should().BeTrue();
        page1.Should().HaveCount(1);

        var (page2, hasMore2) = await store.GetSalesPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ALL,
            page1[0].PurchasedAt, page1[0].OrderId, 1);
        hasMore2.Should().BeFalse();
        page2.Should().HaveCount(1);
        page2[0].OrderId.Should().NotBe(page1[0].OrderId);
    }


    [Fact]
    public async Task GetSalesPage_DoesNotExposeStripeSessionOrUserId()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, version) = await SeedSellerBuyerAsset(db, "nosensitive");

        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 10m, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var (items, _) = await store.GetSalesPage(
            seller.Id,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            AnalyticsProductTypeFilter.ALL,
            null, null, 10);

        items.Should().HaveCount(1);
        var item = items[0];
        item.ProductKind.Should().Be(AnalyticsProductKind.ASSET);
        item.ProductId.Should().Be(asset.Id);
        item.Units.Should().Be(1);
        item.GrossRevenue.Should().Be(10m);
    }


    [Fact]
    public async Task GetOverviewSnapshot_Ratings_WithReviews_ReturnsAverageAndNewCount()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, _) = await SeedSellerBuyerAsset(db, "rating");

        var oldReview = TestData.CreateReview(buyer.Id, asset.Id, 4);
        oldReview.CreatedAt = new DateTimeOffset(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);
        db.Reviews.Add(oldReview);
        await db.SaveChangesAsync();

        var buyer2 = TestData.CreateUser("buyer2-rating", "buyer2-rating@test.local");
        db.Users.Add(buyer2);
        await db.SaveChangesAsync();

        var newReview = TestData.CreateReview(buyer2.Id, asset.Id);
        newReview.CreatedAt = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero);
        db.Reviews.Add(newReview);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero);

        var ratings = (await GetSnapshot(store, seller.Id, from, to)).CurrentRatings;

        ratings.AverageRating.Should().BeApproximately(4.5, 0.001);
        ratings.NewReviews.Should().Be(1);
    }


    [Fact]
    public async Task GetSalesPage_ProductTypeFilter_AssetBundleAll()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, version) = await SeedSellerBuyerAsset(db, "salestype");

        var from = new DateTimeOffset(2024, 8, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero);

        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 10m,
            new DateTimeOffset(2024, 8, 10, 0, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();
        await AddBundleOrder(db, buyer.Id, seller.Id, [(asset, version, 7m)], 7m,
            new DateTimeOffset(2024, 8, 15, 0, 0, 0, TimeSpan.Zero));

        var store = new SellerAnalyticsStore(db);

        var (assets, _) = await store.GetSalesPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ASSET, null, null, 10);
        assets.Should().HaveCount(1);
        assets[0].ProductKind.Should().Be(AnalyticsProductKind.ASSET);

        var (bundles, _) = await store.GetSalesPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.BUNDLE, null, null, 10);
        bundles.Should().HaveCount(1);
        bundles[0].ProductKind.Should().Be(AnalyticsProductKind.BUNDLE);

        var (all, _) = await store.GetSalesPage(
            seller.Id, from, to, AnalyticsProductTypeFilter.ALL, null, null, 10);
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task OpenSalesExportSession_OrdersByPurchasedAtDescThenOrderIdDesc()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, version) = await SeedSellerBuyerAsset(db, "exportorder");

        var t1 = new DateTimeOffset(2024, 9, 1, 1, 0, 0, TimeSpan.Zero);
        var t2 = new DateTimeOffset(2024, 9, 1, 2, 0, 0, TimeSpan.Zero);
        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 10m, t1);
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 20m, t2);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 9, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero);

        var rows = await CollectExportRows(store, seller.Id, from, to, AnalyticsProductTypeFilter.ALL);

        rows.Should().HaveCount(2);
        rows[0].PurchasedAt.Should().Be(t2);
        rows[1].PurchasedAt.Should().Be(t1);
        rows[0].OrderId.Should().NotBe(rows[1].OrderId);
    }

    [Fact]
    public async Task OpenSalesExportSession_ProductTypeFilter_RespectsAssetBundleAll()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, version) = await SeedSellerBuyerAsset(db, "exportfilter");

        var from = new DateTimeOffset(2024, 10, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 11, 1, 0, 0, 0, TimeSpan.Zero);

        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 10m,
            new DateTimeOffset(2024, 10, 5, 0, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();
        await AddBundleOrder(db, buyer.Id, seller.Id, [(asset, version, 7m)], 7m,
            new DateTimeOffset(2024, 10, 6, 0, 0, 0, TimeSpan.Zero));

        var store = new SellerAnalyticsStore(db);

        var assets = await CollectExportRows(store, seller.Id, from, to, AnalyticsProductTypeFilter.ASSET);
        assets.Should().HaveCount(1);
        assets[0].ProductType.Should().Be("ASSET");

        var bundles = await CollectExportRows(store, seller.Id, from, to, AnalyticsProductTypeFilter.BUNDLE);
        bundles.Should().HaveCount(1);
        bundles[0].ProductType.Should().Be("BUNDLE");

        var all = await CollectExportRows(store, seller.Id, from, to, AnalyticsProductTypeFilter.ALL);
        all.Should().HaveCount(2);
    }

    [Fact]
    public async Task OpenSalesExportSession_SellerIsolation_ExcludesOtherSellerOrders()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (sellerA, buyer, _, assetA, versionA) = await SeedSellerBuyerAsset(db, "exportisoA");
        var sellerB = TestData.CreateUser("sellerexportisoB", "sellerexportisoB@test.local");
        db.Users.Add(sellerB);
        await db.SaveChangesAsync();

        var from = new DateTimeOffset(2024, 11, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 12, 1, 0, 0, 0, TimeSpan.Zero);
        AddDirectOrder(db, buyer.Id, assetA, versionA, sellerA.Id, 10m,
            new DateTimeOffset(2024, 11, 5, 0, 0, 0, TimeSpan.Zero));
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var rowsB = await CollectExportRows(store, sellerB.Id, from, to, AnalyticsProductTypeFilter.ALL);
        rowsB.Should().BeEmpty();
    }

    [Fact]
    public async Task OpenSalesExportSession_WhenWithinLimit_ExceedsMaxIsFalse()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, version) = await SeedSellerBuyerAsset(db, "exportcapok");

        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 10m, DateTimeOffset.UtcNow);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        await using var session = await store.OpenSalesExportSession(
            seller.Id,
            DateTimeOffset.UtcNow.AddDays(-1),
            DateTimeOffset.UtcNow.AddDays(1),
            AnalyticsProductTypeFilter.ALL);

        session.ExceedsMax.Should().BeFalse();
    }

    private static async Task<List<AnalyticsSalesExportRow>> CollectExportRows(
        ISellerAnalyticsStore store,
        Guid sellerId,
        DateTimeOffset from,
        DateTimeOffset to,
        AnalyticsProductTypeFilter productType)
    {
        await using var session = await store.OpenSalesExportSession(sellerId, from, to, productType);
        session.ExceedsMax.Should().BeFalse();

        var rows = new List<AnalyticsSalesExportRow>();
        await foreach (var row in session.ReadRows())
        {
            rows.Add(row);
        }

        return rows;
    }


    [Fact]
    public async Task GetOverviewSnapshot_UtcBoundary_FromInclusiveToExclusive()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, version) = await SeedSellerBuyerAsset(db, "boundary");

        var from = new DateTimeOffset(2024, 5, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 5, 2, 0, 0, 0, TimeSpan.Zero);

        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 10m, from);
        await db.SaveChangesAsync();
        AddDirectOrder(db, buyer.Id, asset, version, seller.Id, 20m, to);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var facts = (await GetSnapshot(store, seller.Id, from, to)).CurrentFacts;

        facts.Orders.Should().Be(1);
        facts.GrossRevenue.Should().Be(10m);
    }


    [Fact]
    public async Task GetOverviewSnapshot_Ratings_CurrentAndComparisonFilters()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, _) = await SeedSellerBuyerAsset(db, "dualrate");

        var comparisonReview = TestData.CreateReview(buyer.Id, asset.Id, 3);
        comparisonReview.CreatedAt = new DateTimeOffset(2024, 5, 15, 0, 0, 0, TimeSpan.Zero);
        db.Reviews.Add(comparisonReview);
        await db.SaveChangesAsync();

        var buyer2 = TestData.CreateUser("buyer2-dualrate", "buyer2-dualrate@test.local");
        db.Users.Add(buyer2);
        await db.SaveChangesAsync();

        var currentReview = TestData.CreateReview(buyer2.Id, asset.Id);
        currentReview.CreatedAt = new DateTimeOffset(2024, 6, 15, 0, 0, 0, TimeSpan.Zero);
        db.Reviews.Add(currentReview);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 6, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 7, 1, 0, 0, 0, TimeSpan.Zero);
        var snapshot = await GetSnapshot(store, seller.Id, from, to);

        snapshot.CurrentRatings.AverageRating.Should().BeApproximately(4.0, 0.001);
        snapshot.CurrentRatings.NewReviews.Should().Be(1);
        snapshot.ComparisonRatings.AverageRating.Should().BeApproximately(4.0, 0.001);
        snapshot.ComparisonRatings.NewReviews.Should().Be(1);
    }


    [Fact]
    public async Task GetProductsPage_InvalidProductType_Throws()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, _, _, _, _) = await SeedSellerBuyerAsset(db, "badtype");
        var store = new SellerAnalyticsStore(db);

        var act = () => store.GetProductsPage(
            seller.Id,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero),
            new DateTimeOffset(2024, 2, 1, 0, 0, 0, TimeSpan.Zero),
            (AnalyticsProductTypeFilter)999,
            1, 10,
            AnalyticsProductSort.REVENUE,
            AnalyticsSortDirection.DESC);

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>()
            .WithParameterName("productType");
    }


    [Fact]
    public async Task GetOverviewSnapshot_FullCoverage_IssuesExactlyNineReaderCommands()
    {
        var interceptor = new OverviewReaderCountingInterceptor();
        await using var db = await fixture.CreateCleanDbContext(b => b.AddInterceptors(interceptor));
        var (seller, _, _, asset, _) = await SeedSellerBuyerAsset(db, "overview-rt");

        var eventStore = new AnalyticsEventStore(db);
        var occurredAt = new DateTimeOffset(2024, 1, 5, 12, 0, 0, TimeSpan.Zero);
        await eventStore.TryInsert(CreateAssetView(seller.Id, asset.Id, occurredAt));

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 1, 25, 0, 0, 0, TimeSpan.Zero);

        interceptor.Reset();
        await GetSnapshot(store, seller.Id, from, to);

        interceptor.ReaderCommandCount.Should().Be(9);
    }

    private static AnalyticsEvent CreateAssetView(Guid sellerId, Guid assetId, DateTimeOffset occurredAt) =>
        new()
        {
            Id = Guid.NewGuid(),
            EventType = AnalyticsEventType.ASSET_VIEW,
            OccurredAt = occurredAt,
            SellerId = sellerId,
            VisitorId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            AssetId = assetId,
            Source = AnalyticsTrafficSource.CATALOG,
            DeviceClass = AnalyticsDeviceClass.DESKTOP
        };

    [Fact]
    public async Task GetOverviewSnapshot_CommerceOnly_IssuesExactlyEightReaderCommands()
    {
        var interceptor = new OverviewReaderCountingInterceptor();
        await using var db = await fixture.CreateCleanDbContext(b => b.AddInterceptors(interceptor));
        var (seller, _, _, _, _) = await SeedSellerBuyerAsset(db, "qcount");

        var store = new SellerAnalyticsStore(db);
        var from = new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var to = new DateTimeOffset(2024, 1, 11, 0, 0, 0, TimeSpan.Zero);

        interceptor.Reset();
        await GetSnapshot(store, seller.Id, from, to);

        interceptor.ReaderCommandCount.Should().Be(8);
    }

    private sealed class OverviewReaderCountingInterceptor : DbCommandInterceptor
    {
        public int ReaderCommandCount { get; private set; }

        public void Reset() => ReaderCommandCount = 0;

        public override InterceptionResult<DbDataReader> ReaderExecuting(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result)
        {
            CountReader(command.CommandText);
            return base.ReaderExecuting(command, eventData, result);
        }

        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            CountReader(command.CommandText);
            return base.ReaderExecutingAsync(command, eventData, result, cancellationToken);
        }

        private void CountReader(string sql)
        {
            if (sql.TrimStart().StartsWith("SET TRANSACTION", StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            ReaderCommandCount++;
        }
    }
}
