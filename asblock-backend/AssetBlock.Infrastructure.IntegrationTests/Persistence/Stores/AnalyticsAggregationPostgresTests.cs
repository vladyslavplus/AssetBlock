using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class AnalyticsAggregationPostgresTests(PostgresFixture fixture)
{
    private static readonly DateOnly _dayOne = new(2024, 6, 10);
    private static readonly DateOnly _dayTwo = new(2024, 6, 11);
    private const int COMMAND_TIMEOUT_SECONDS = 120;

    [Fact]
    public async Task TryAcquireAndRecomputeDaily_WhenEventsSeeded_ShouldMatchRawCountsAndBeIdempotentOnRerun()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var store = new AnalyticsEventStore(db);

        await SeedAssetViewBurst(store, sellerId, assetId, _dayOne, eventCount: 120);
        await SeedAssetViewBurst(store, sellerId, assetId, _dayTwo, eventCount: 80);

        var updatedAt = DateTimeOffset.UtcNow;
        var first = await store.TryAcquireAndRecomputeDaily(
            _dayTwo, _dayOne, updatedAt, COMMAND_TIMEOUT_SECONDS);
        first.Outcome.Should().Be(AnalyticsDailyRecomputeOutcome.COMPLETED);

        var dayOneRow = await db.SellerAnalyticsDaily.AsNoTracking()
            .SingleAsync(r => r.SellerId == sellerId && r.DayUtc == _dayOne);
        dayOneRow.AssetViews.Should().Be(120);
        dayOneRow.UniqueVisitors.Should().Be(120);

        var dayTwoRow = await db.SellerAnalyticsDaily.AsNoTracking()
            .SingleAsync(r => r.SellerId == sellerId && r.DayUtc == _dayTwo);
        dayTwoRow.AssetViews.Should().Be(80);
        dayTwoRow.UniqueVisitors.Should().Be(80);

        var productRows = await db.ProductAnalyticsDaily.AsNoTracking()
            .Where(r => r.SellerId == sellerId)
            .ToListAsync();
        productRows.Should().HaveCount(2);
        productRows.Sum(r => r.Views).Should().Be(200);

        var second = await store.TryAcquireAndRecomputeDaily(
            _dayTwo, _dayOne, updatedAt.AddMinutes(1), COMMAND_TIMEOUT_SECONDS);
        second.Outcome.Should().Be(AnalyticsDailyRecomputeOutcome.COMPLETED);

        var dayOneAfterRerun = await db.SellerAnalyticsDaily.AsNoTracking()
            .SingleAsync(r => r.SellerId == sellerId && r.DayUtc == _dayOne);
        dayOneAfterRerun.AssetViews.Should().Be(dayOneRow.AssetViews);
        dayOneAfterRerun.UniqueVisitors.Should().Be(dayOneRow.UniqueVisitors);
        dayTwoRow.AssetViews.Should().Be(80);
    }

    [Fact]
    public async Task TryAcquireAndRecomputeDaily_WhenEventsDeleted_ShouldRemoveStaleDailyGroups()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var store = new AnalyticsEventStore(db);

        await SeedAssetViewBurst(store, sellerId, assetId, _dayOne, eventCount: 40);

        await store.TryAcquireAndRecomputeDaily(
            _dayOne, _dayOne.AddDays(-1), DateTimeOffset.UtcNow, COMMAND_TIMEOUT_SECONDS);

        (await db.SellerAnalyticsDaily.CountAsync(r => r.SellerId == sellerId)).Should().Be(1);

        await db.Database.ExecuteSqlAsync(
            $"""
            DELETE FROM analytics_events
            WHERE "SellerId" = {sellerId}
              AND "OccurredAt" >= {_dayStart(_dayOne)}
              AND "OccurredAt" < {_dayStart(_dayOne.AddDays(1))}
            """);

        await store.TryAcquireAndRecomputeDaily(
            _dayOne, _dayOne.AddDays(-1), DateTimeOffset.UtcNow, COMMAND_TIMEOUT_SECONDS);

        (await db.SellerAnalyticsDaily.CountAsync(r => r.SellerId == sellerId)).Should().Be(0);
        (await db.ProductAnalyticsDaily.CountAsync(r => r.SellerId == sellerId)).Should().Be(0);
    }

    [Fact]
    public async Task TryAcquireAndRecomputeDaily_WhenTwoWorkersRunConcurrently_ShouldSkipOne()
    {
        await using var setupDb = await fixture.CreateCleanDbContext();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var store = new AnalyticsEventStore(setupDb);
        await SeedAssetViewBurst(store, sellerId, assetId, _dayOne, eventCount: 500);

        var gate = new Barrier(2);
        var results = new AnalyticsDailyRecomputeResult[2];

        var tasks = Enumerable.Range(0, 2).Select(i => Task.Run(async () =>
        {
            await using var db = fixture.CreateDbContext();
            var workerStore = new AnalyticsEventStore(db);
            gate.SignalAndWait();
            results[i] = await workerStore.TryAcquireAndRecomputeDaily(
                _dayOne,
                _dayOne.AddDays(-1),
                DateTimeOffset.UtcNow,
                COMMAND_TIMEOUT_SECONDS);
        })).ToArray();

        await Task.WhenAll(tasks);

        results.Count(r => r.Outcome == AnalyticsDailyRecomputeOutcome.COMPLETED).Should().Be(1);
        results.Count(r => r.Outcome == AnalyticsDailyRecomputeOutcome.SKIPPED).Should().Be(1);
    }

    [Fact]
    public async Task DeleteExpiredEvents_WhenCutoffApplied_ShouldDeleteOlderRowsAndRetainDailyRollups()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var store = new AnalyticsEventStore(db);
        var cutoff = new DateTimeOffset(2024, 1, 15, 0, 0, 0, TimeSpan.Zero);

        await store.TryInsert(CreateAssetView(sellerId, assetId, cutoff.AddDays(-1)));
        await store.TryInsert(CreateAssetView(sellerId, assetId, cutoff));
        await store.TryInsert(CreateAssetView(sellerId, assetId, cutoff.AddHours(1)));

        await store.TryAcquireAndRecomputeDaily(
            DateOnly.FromDateTime(cutoff.UtcDateTime),
            DateOnly.FromDateTime(cutoff.UtcDateTime.AddDays(-1)),
            DateTimeOffset.UtcNow,
            COMMAND_TIMEOUT_SECONDS);

        var retention = await store.TryAcquireAndDeleteExpiredEvents(
            cutoff, batchSize: 100, maxBatches: 5, COMMAND_TIMEOUT_SECONDS);

        retention.DeletedCount.Should().Be(1);
        (await db.AnalyticsEvents.AsNoTracking().CountAsync()).Should().Be(2);
        (await db.SellerAnalyticsDaily.AsNoTracking().CountAsync()).Should().BeGreaterThan(0);

        var atCutoff = await db.AnalyticsEvents.AsNoTracking()
            .SingleAsync(e => e.OccurredAt == cutoff);
        atCutoff.Should().NotBeNull();
    }

    [Fact]
    public async Task EngagementOverview_ExactRangeUniqueVisitors_ShouldNotEqualSumOfDailyUniqueVisitors()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var seller = TestData.CreateUser("overlap-seller", "overlap-seller@test.local");
        var category = TestData.CreateCategory("overlap-cat", "overlap-cat");
        db.Users.Add(seller);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var asset = TestData.CreateAsset(seller.Id, category.Id, "Overlap Asset", 5m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var sharedVisitor = Guid.NewGuid();
        var store = new AnalyticsEventStore(db);
        await store.TryInsert(CreateAssetView(
            seller.Id, asset.Id, _dayStart(_dayOne), sharedVisitor));
        await store.TryInsert(CreateAssetView(
            seller.Id, asset.Id, _dayStart(_dayTwo), sharedVisitor));

        await store.TryAcquireAndRecomputeDaily(
            _dayTwo, _dayOne, DateTimeOffset.UtcNow, COMMAND_TIMEOUT_SECONDS);

        var dailySum = await db.SellerAnalyticsDaily.AsNoTracking()
            .Where(r => r.SellerId == seller.Id && r.DayUtc >= _dayOne && r.DayUtc <= _dayTwo)
            .SumAsync(r => r.UniqueVisitors);
        dailySum.Should().Be(2);

        var analyticsStore = new SellerAnalyticsStore(db);
        var from = _dayStart(_dayOne);
        var to = _dayStart(_dayTwo.AddDays(1));
        var snapshot = await analyticsStore.GetOverviewSnapshot(
            seller.Id, from, to, from.AddDays(-2), from, topN: 5, AnalyticsGranularity.DAY);

        snapshot.CurrentEngagement!.UniqueVisitors.Should().Be(1);
        snapshot.CurrentEngagement.UniqueVisitors.Should().NotBe(dailySum);
    }

    [Fact]
    public async Task TryAcquireAndRecomputeDaily_WhenDownloadOnlyVisitorExists_ShouldCountViewVisitorsOnlyInUniqueVisitors()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var seller = TestData.CreateUser("uv-roll", "uv-roll@test.local");
        var category = TestData.CreateCategory("uv-roll-cat", "uv-roll-cat");
        db.Users.Add(seller);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var asset = TestData.CreateAsset(seller.Id, category.Id, "UV Roll Asset", 10m);
        var version = TestData.CreateAssetVersion(asset.Id);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var store = new AnalyticsEventStore(db);
        var viewVisitor = Guid.NewGuid();
        var downloadVisitor = Guid.NewGuid();

        await store.TryInsert(CreateAssetView(seller.Id, asset.Id, _dayStart(_dayOne), viewVisitor));
        await store.TryInsert(new AnalyticsEvent
        {
            Id = Guid.NewGuid(),
            EventType = AnalyticsEventType.DOWNLOAD_REQUESTED,
            OccurredAt = _dayStart(_dayOne).AddHours(2),
            SellerId = seller.Id,
            VisitorId = downloadVisitor,
            SessionId = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            Source = AnalyticsTrafficSource.DIRECT_INTERNAL,
            DeviceClass = AnalyticsDeviceClass.DESKTOP
        });

        await store.TryAcquireAndRecomputeDaily(
            _dayOne, _dayOne.AddDays(-1), DateTimeOffset.UtcNow, COMMAND_TIMEOUT_SECONDS);

        var row = await db.SellerAnalyticsDaily.AsNoTracking()
            .SingleAsync(r => r.SellerId == seller.Id && r.DayUtc == _dayOne);
        row.UniqueVisitors.Should().Be(1);
        row.DownloadRequests.Should().Be(1);
        row.AssetViews.Should().Be(1);

        var analyticsStore = new SellerAnalyticsStore(db);
        var from = _dayStart(_dayOne);
        var to = _dayStart(_dayOne.AddDays(1));
        var snapshot = await analyticsStore.GetOverviewSnapshot(
            seller.Id, from, to, from.AddDays(-1), from, topN: 5, AnalyticsGranularity.DAY);

        snapshot.CurrentEngagement!.UniqueVisitors.Should().Be(1);
    }

    [Fact]
    public async Task GetOverviewSnapshot_TrackedFunnel_ShouldBeMonotonic()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var (seller, buyer, _, asset, version) = await SeedSellerAsset(db);

        var sessionViewOnly = Guid.NewGuid();
        var sessionCheckoutOnly = Guid.NewGuid();
        var sessionCompleted = Guid.NewGuid();
        var from = _dayStart(_dayOne);
        var to = _dayStart(_dayOne.AddDays(1));

        var eventStore = new AnalyticsEventStore(db);
        await eventStore.TryInsert(CreateAssetView(
            seller.Id, asset.Id, from, visitorId: Guid.NewGuid(), sessionId: sessionViewOnly));
        await eventStore.TryInsert(CreateAssetView(
            seller.Id, asset.Id, from.AddHours(1), visitorId: Guid.NewGuid(), sessionId: sessionCompleted));

        AddAttributedCheckoutIntent(
            db, buyer.Id, asset, version, seller.Id, from.AddHours(3),
            sessionId: sessionCheckoutOnly,
            completeOrder: false);
        AddAttributedCheckoutIntent(
            db, buyer.Id, asset, version, seller.Id, from.AddHours(4),
            sessionId: sessionCompleted,
            completeOrder: true);
        await db.SaveChangesAsync();

        var store = new SellerAnalyticsStore(db);
        var snapshot = await store.GetOverviewSnapshot(
            seller.Id, from, to, from.AddDays(-10), from, topN: 5, AnalyticsGranularity.DAY);

        snapshot.TrackedFunnel.Should().NotBeNull();
        var funnel = snapshot.TrackedFunnel!;
        funnel.ViewSessions.Should().BeGreaterThanOrEqualTo(funnel.CheckoutSessions);
        funnel.CheckoutSessions.Should().BeGreaterThanOrEqualTo(funnel.CompletedSessions);
        funnel.ViewSessions.Should().Be(2);
        funnel.CheckoutSessions.Should().Be(1);
        funnel.CompletedSessions.Should().Be(1);
    }

    [Fact]
    public async Task Explain_SellerEngagementQuery_ShouldReferenceExpectedAnalyticsIndexes()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var sellerId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var store = new AnalyticsEventStore(db);
        await SeedAssetViewBurst(store, sellerId, assetId, _dayOne, eventCount: 800);

        var from = _dayStart(_dayOne);
        var to = _dayStart(_dayOne.AddDays(1));
        var plan = await db.Database.SqlQueryRaw<string>(
                """
                EXPLAIN SELECT COUNT(*)::bigint
                FROM analytics_events ae
                WHERE ae."SellerId" = {0}
                  AND ae."OccurredAt" >= {1}
                  AND ae."OccurredAt" < {2}
                  AND ae."EventType" IN ('ASSET_VIEW', 'BUNDLE_VIEW')
                """,
                sellerId,
                from,
                to)
            .ToListAsync();

        var planText = string.Join('\n', plan);
        planText.Should().Contain("IX_analytics_events_SellerId");
    }

    private static async Task SeedAssetViewBurst(AnalyticsEventStore store,
        Guid sellerId,
        Guid assetId,
        DateOnly day,
        int eventCount)
    {
        var dayStart = _dayStart(day);
        for (var i = 0; i < eventCount; i++)
        {
            var occurredAt = dayStart.AddMinutes(i % 720);
            await store.TryInsert(CreateAssetView(
                sellerId,
                assetId,
                occurredAt,
                visitorId: Guid.NewGuid(),
                sessionId: Guid.NewGuid(),
                id: Guid.NewGuid()));
        }
    }

    private static AnalyticsEvent CreateAssetView(
        Guid sellerId,
        Guid assetId,
        DateTimeOffset occurredAt,
        Guid? visitorId = null,
        Guid? sessionId = null,
        Guid? id = null) =>
        new()
        {
            Id = id ?? Guid.NewGuid(),
            EventType = AnalyticsEventType.ASSET_VIEW,
            OccurredAt = occurredAt,
            SellerId = sellerId,
            VisitorId = visitorId ?? Guid.NewGuid(),
            SessionId = sessionId ?? Guid.NewGuid(),
            AssetId = assetId,
            Source = AnalyticsTrafficSource.CATALOG,
            DeviceClass = AnalyticsDeviceClass.DESKTOP
        };

    private static DateTimeOffset _dayStart(DateOnly day) =>
        new(day.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc));

    private static async Task<(User Seller, User Buyer, Category Category, Asset Asset, AssetVersion Version)>
        SeedSellerAsset(ApplicationDbContext db)
    {
        var seller = TestData.CreateUser("funnel-seller", "funnel-seller@test.local");
        var buyer = TestData.CreateUser("funnel-buyer", "funnel-buyer@test.local");
        var category = TestData.CreateCategory("funnel-cat", "funnel-cat");
        db.Users.AddRange(seller, buyer);
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        var asset = TestData.CreateAsset(seller.Id, category.Id, "Funnel Asset", 12m);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();

        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        return (seller, buyer, category, asset, version);
    }

    private static void AddAttributedCheckoutIntent(
        ApplicationDbContext db,
        Guid buyerId,
        Asset asset,
        AssetVersion version,
        Guid sellerId,
        DateTimeOffset createdAt,
        Guid sessionId,
        bool completeOrder)
    {
        var intentId = Guid.NewGuid();
        var stripeSessionId = $"test-stripe-{Guid.NewGuid():N}";

        db.CheckoutIntents.Add(new CheckoutIntent
        {
            Id = intentId,
            UserId = buyerId,
            AssetId = asset.Id,
            ProductTitle = asset.Title,
            AmountTotal = asset.Price,
            Currency = "usd",
            StripeSessionId = stripeSessionId,
            Status = completeOrder ? CheckoutIntentStatus.COMPLETED : CheckoutIntentStatus.PENDING,
            CreatedAt = createdAt,
            ExpiresAt = createdAt.AddHours(1),
            CompletedAt = completeOrder ? createdAt : null,
            AttributionSource = AnalyticsTrafficSource.CATALOG,
            AnalyticsVisitorId = Guid.NewGuid(),
            AnalyticsSessionId = sessionId
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
            ListPrice = asset.Price,
            AllocatedPrice = asset.Price,
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
            AmountPaid = asset.Price,
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
            ListPrice = asset.Price,
            PricePaid = asset.Price,
            LicenseCode = AssetLicenseCode.PERSONAL,
            LicenseTemplateVersion = "1.0",
            LicenseDisplayName = "Personal",
            LicenseTerms = "terms"
        });
    }
}
