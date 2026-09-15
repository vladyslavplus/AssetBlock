using System.Text.Json;
using System.Text.RegularExpressions;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class RecommendationPersonalizationStorePostgresTests(
    PostgresFixture fixture,
    Xunit.Abstractions.ITestOutputHelper output)
{
    private static readonly DateTimeOffset _now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task SetPersonalized_WhenFirstOptIn_ShouldCreateRowWithOptedInAt()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User user = await SeedUser(db);
        var store = new RecommendationPersonalizationStore(db);

        UserRecommendationPreferences saved = await store.SetPersonalized(user.Id, true, _now);

        saved.IsPersonalized.Should().BeTrue();
        saved.OptedInAt.Should().Be(_now);
        (await db.UserRecommendationPreferences.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SetPersonalized_WhenAlreadyOn_ShouldKeepOriginalOptedInAt()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User user = await SeedUser(db);
        var store = new RecommendationPersonalizationStore(db);
        DateTimeOffset first = _now.AddDays(-5);
        await store.SetPersonalized(user.Id, true, first);

        UserRecommendationPreferences saved = await store.SetPersonalized(user.Id, true, _now);

        saved.IsPersonalized.Should().BeTrue();
        saved.OptedInAt.Should().Be(first);
        (await db.UserRecommendationPreferences.CountAsync()).Should().Be(1);
    }

    [Fact]
    public async Task SetPersonalized_WhenOptOut_ShouldClearFlagAndDeleteAllAffinityRows()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User user = await SeedUser(db);
        var store = new RecommendationPersonalizationStore(db);
        await store.SetPersonalized(user.Id, true, _now.AddDays(-1));
        db.UserTagAffinities.Add(new UserTagAffinity
        {
            UserId = user.Id,
            TagId = Guid.NewGuid(),
            Purchases = 2,
            Reviews = 1,
            UpdatedAt = _now
        });
        db.UserSourceClickAffinities.Add(new UserSourceClickAffinity
        {
            UserId = user.Id,
            SourceAssetId = Guid.NewGuid(),
            TargetAssetId = Guid.NewGuid(),
            Clicks = 3,
            LastClickedAt = _now,
            UpdatedAt = _now
        });
        await db.SaveChangesAsync();

        UserRecommendationPreferences saved = await store.SetPersonalized(user.Id, false, _now);

        saved.IsPersonalized.Should().BeFalse();
        saved.OptedInAt.Should().BeNull();
        (await db.UserTagAffinities.CountAsync(a => a.UserId == user.Id)).Should().Be(0);
        (await db.UserSourceClickAffinities.CountAsync(a => a.UserId == user.Id)).Should().Be(0);
        // Preference row itself survives so "off" stays off.
        (await db.UserRecommendationPreferences.CountAsync(p => p.UserId == user.Id)).Should().Be(1);
    }

    [Fact]
    public async Task SetPersonalized_WhenConcurrentFirstOptIn_ShouldKeepSingleRow()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User user = await SeedUser(db);
        await using ApplicationDbContext db2 = fixture.CreateDbContext();
        var first = new RecommendationPersonalizationStore(db);
        var second = new RecommendationPersonalizationStore(db2);

        await Task.WhenAll(
            first.SetPersonalized(user.Id, true, _now),
            second.SetPersonalized(user.Id, true, _now));

        (await db.UserRecommendationPreferences.CountAsync(p => p.UserId == user.Id)).Should().Be(1);
        (await db.UserRecommendationPreferences.AsNoTracking().SingleAsync(p => p.UserId == user.Id)).IsPersonalized.Should().BeTrue();
    }

    [Fact]
    public async Task GetPreferences_WhenMissing_ShouldReturnNull()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new RecommendationPersonalizationStore(db);

        (await store.GetPreferences(Guid.NewGuid())).Should().BeNull();
    }

    [Fact]
    public async Task TryRecomputeUser_WhenNotOptedIn_ShouldSkipAndWriteNothing()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User user = await SeedUser(db);
        var store = new RecommendationPersonalizationStore(db);

        (await store.TryRecomputeUser(user.Id, _now)).Should().BeFalse();
        (await db.UserTagAffinities.CountAsync()).Should().Be(0);
        (await db.UserSourceClickAffinities.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TryRecomputeUser_ShouldAggregateClicksPurchasesAndReviews()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        Tag shared = TestData.CreateTag("shared");
        Tag other = TestData.CreateTag("other");
        db.Tags.AddRange(shared, other);
        Asset purchased = AddReadyAsset(db, author, category, "Purchased", tags: [shared, other]);
        Asset reviewed = AddReadyAsset(db, author, category, "Reviewed", tags: [shared]);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset target = AddReadyAsset(db, author, category, "Target");
        await db.SaveChangesAsync();

        AssetVersion version = await db.AssetVersions.AsNoTracking().FirstAsync(v => v.AssetId == purchased.Id);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(user.Id, purchased.Id, version.Id), "Purchased", author.Id);
        Review review = TestData.CreateReview(user.Id, reviewed.Id);
        db.Reviews.Add(review);
        DateTimeOffset clickAt = _now.AddDays(-2);
        AddClick(db, user.Id, source.Id, target.Id, clickAt);
        AddClick(db, user.Id, source.Id, target.Id, clickAt.AddHours(1));
        // Another account's and another source's clicks must not leak in.
        User stranger = TestData.CreateUser("stranger", "stranger@example.test");
        db.Users.Add(stranger);
        await db.SaveChangesAsync();
        AddClick(db, stranger.Id, source.Id, target.Id, clickAt);
        AddClick(db, user.Id, Guid.NewGuid(), target.Id, clickAt);
        await db.SaveChangesAsync();

        var store = new RecommendationPersonalizationStore(db);
        await store.SetPersonalized(user.Id, true, _now.AddDays(-10));

        (await store.TryRecomputeUser(user.Id, _now)).Should().BeTrue();

        UserSourceClickAffinity pair = await db.UserSourceClickAffinities.AsNoTracking()
            .SingleAsync(a => a.UserId == user.Id && a.SourceAssetId == source.Id && a.TargetAssetId == target.Id);
        pair.Clicks.Should().Be(2);
        pair.LastClickedAt.Should().Be(clickAt.AddHours(1));
        // The user's click on another source yields its own pair row; the stranger's
        // same-pair click is excluded by the ActorUserId predicate.
        (await db.UserSourceClickAffinities.CountAsync(a => a.UserId == user.Id)).Should().Be(2);

        Dictionary<Guid, (long Purchases, long Reviews)> tags = await db.UserTagAffinities.AsNoTracking()
            .Where(a => a.UserId == user.Id)
            .ToDictionaryAsync(a => a.TagId, a => (a.Purchases, a.Reviews));
        tags.Should().HaveCount(2);
        tags[shared.Id].Should().Be((1, 1));
        tags[other.Id].Should().Be((1, 0));
    }

    [Fact]
    public async Task TryRecomputeUser_WhenRerunWithSameInputs_ShouldBeValueNoOp()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        Tag tag = TestData.CreateTag("tag");
        db.Tags.Add(tag);
        Asset purchased = AddReadyAsset(db, author, category, "Purchased", tags: [tag]);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset target = AddReadyAsset(db, author, category, "Target");
        await db.SaveChangesAsync();
        AssetVersion version = await db.AssetVersions.AsNoTracking().FirstAsync(v => v.AssetId == purchased.Id);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(user.Id, purchased.Id, version.Id), "Purchased", author.Id);
        AddClick(db, user.Id, source.Id, target.Id, _now.AddDays(-1));
        await db.SaveChangesAsync();

        var store = new RecommendationPersonalizationStore(db);
        await store.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await store.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        List<UserTagAffinity> firstTags = await db.UserTagAffinities.AsNoTracking().OrderBy(a => a.TagId).ToListAsync();
        List<UserSourceClickAffinity> firstPairs = await db.UserSourceClickAffinities.AsNoTracking().OrderBy(a => a.TargetAssetId).ToListAsync();

        // Simulated worker restart: recompute again with identical inputs and timestamp.
        (await store.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        List<UserTagAffinity> secondTags = await db.UserTagAffinities.AsNoTracking().OrderBy(a => a.TagId).ToListAsync();
        List<UserSourceClickAffinity> secondPairs = await db.UserSourceClickAffinities.AsNoTracking().OrderBy(a => a.TargetAssetId).ToListAsync();

        secondTags.Should().BeEquivalentTo(firstTags);
        secondPairs.Should().BeEquivalentTo(firstPairs);
    }

    [Fact]
    public async Task TryRecomputeUser_ShouldRetainOnlyCurrentWindowContribution()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        Tag tag = TestData.CreateTag("tag");
        db.Tags.Add(tag);
        Asset asset = AddReadyAsset(db, author, category, "Asset", tags: [tag]);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset target = AddReadyAsset(db, author, category, "Target");
        await db.SaveChangesAsync();

        // Expired click (91 days old) and review must not contribute; fresh click must.
        AddClick(db, user.Id, source.Id, target.Id, _now.AddDays(-91));
        AddClick(db, user.Id, source.Id, target.Id, _now.AddDays(-1));
        Review oldReview = TestData.CreateReview(user.Id, asset.Id);
        oldReview.CreatedAt = _now.AddDays(-100);
        db.Reviews.Add(oldReview);
        await db.SaveChangesAsync();

        var store = new RecommendationPersonalizationStore(db);
        await store.SetPersonalized(user.Id, true, _now.AddDays(-200));

        (await store.TryRecomputeUser(user.Id, _now)).Should().BeTrue();

        UserSourceClickAffinity pair = await db.UserSourceClickAffinities.AsNoTracking().SingleAsync();
        pair.Clicks.Should().Be(1);
        (await db.UserTagAffinities.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TryRecomputeUser_ShouldExcludePreOptInHistory()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        Tag tag = TestData.CreateTag("tag");
        db.Tags.Add(tag);
        Asset asset = AddReadyAsset(db, author, category, "Asset", tags: [tag]);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset target = AddReadyAsset(db, author, category, "Target");
        await db.SaveChangesAsync();
        AssetVersion version = await db.AssetVersions.AsNoTracking().FirstAsync(v => v.AssetId == asset.Id);
        TestData.AddCompletedPurchase(
            db,
            TestData.CreatePurchase(user.Id, asset.Id, version.Id, purchasedAt: _now.AddDays(-20)),
            "Asset",
            author.Id);
        AddClick(db, user.Id, source.Id, target.Id, _now.AddDays(-20));
        await db.SaveChangesAsync();

        var store = new RecommendationPersonalizationStore(db);
        // Opt-in happened 10 days ago: 20-day-old history must not backfill.
        await store.SetPersonalized(user.Id, true, _now.AddDays(-10));

        (await store.TryRecomputeUser(user.Id, _now)).Should().BeTrue();

        (await db.UserTagAffinities.CountAsync()).Should().Be(0);
        (await db.UserSourceClickAffinities.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task TryRecomputeUser_WhenTagVanishes_ShouldDeleteStaleAffinityRow()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        Tag tag = TestData.CreateTag("tag");
        db.Tags.Add(tag);
        Asset asset = AddReadyAsset(db, author, category, "Asset", tags: [tag]);
        await db.SaveChangesAsync();
        AssetVersion version = await db.AssetVersions.AsNoTracking().FirstAsync(v => v.AssetId == asset.Id);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(user.Id, asset.Id, version.Id), "Asset", author.Id);
        await db.SaveChangesAsync();

        var store = new RecommendationPersonalizationStore(db);
        await store.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await store.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        (await db.UserTagAffinities.CountAsync()).Should().Be(1);

        // Taxonomy edit restates the window: the tag is gone, so the row must go too.
        db.AssetTags.RemoveRange(db.AssetTags.Where(at => at.AssetId == asset.Id));
        await db.SaveChangesAsync();

        (await store.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        (await db.UserTagAffinities.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task OptOut_DuringRecompute_ShouldEndWithZeroAffinityRows()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        Tag tag = TestData.CreateTag("tag");
        db.Tags.Add(tag);
        Asset asset = AddReadyAsset(db, author, category, "Asset", tags: [tag]);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset target = AddReadyAsset(db, author, category, "Target");
        await db.SaveChangesAsync();
        AssetVersion version = await db.AssetVersions.AsNoTracking().FirstAsync(v => v.AssetId == asset.Id);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(user.Id, asset.Id, version.Id), "Asset", author.Id);
        AddClick(db, user.Id, source.Id, target.Id, _now.AddDays(-1));
        await db.SaveChangesAsync();

        await using ApplicationDbContext db2 = fixture.CreateDbContext();
        var recomputeStore = new RecommendationPersonalizationStore(db);
        var optOutStore = new RecommendationPersonalizationStore(db2);
        await recomputeStore.SetPersonalized(user.Id, true, _now.AddDays(-10));

        // Either interleaving must end opted-out with zero affinity rows.
        await Task.WhenAll(
            recomputeStore.TryRecomputeUser(user.Id, _now),
            optOutStore.SetPersonalized(user.Id, false, _now));

        UserRecommendationPreferences? preferences = await db.UserRecommendationPreferences
            .AsNoTracking()
            .SingleOrDefaultAsync(p => p.UserId == user.Id);
        preferences.Should().NotBeNull();
        preferences.IsPersonalized.Should().BeFalse();
        (await db.UserTagAffinities.CountAsync(a => a.UserId == user.Id)).Should().Be(0);
        (await db.UserSourceClickAffinities.CountAsync(a => a.UserId == user.Id)).Should().Be(0);
    }

    [Fact]
    public async Task TryRecomputeBatch_ShouldRecomputeOptedInUsersOnly()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User optedIn = await SeedUser(db, "optedin", "optedin@example.test");
        User optedOut = await SeedUser(db, "optedout", "optedout@example.test");
        var store = new RecommendationPersonalizationStore(db);
        await store.SetPersonalized(optedIn.Id, true, _now.AddDays(-1));
        await store.SetPersonalized(optedOut.Id, true, _now.AddDays(-1));
        await store.SetPersonalized(optedOut.Id, false, _now);

        PersonalRecomputeBatchResult result = await store.TryRecomputeBatch(_now, 25, 120);

        result.Outcome.Should().Be(AnalyticsDailyRecomputeOutcome.COMPLETED);
        result.UsersRecomputed.Should().Be(1);
    }

    [Fact]
    public async Task TryRecomputeBatch_WhenLockHeldByAnotherContext_ShouldSkipAndReleaseCleanly()
    {
        await using ApplicationDbContext setupDb = await fixture.CreateCleanDbContext();
        User user = await SeedUser(setupDb, "batch", "batch@example.test");
        var setupStore = new RecommendationPersonalizationStore(setupDb);
        await setupStore.SetPersonalized(user.Id, true, _now.AddDays(-1));

        // Hold the session advisory lock explicitly on a pinned connection, so the
        // worker below deterministically observes a held lock (no Barrier race).
        await using ApplicationDbContext holderDb = fixture.CreateDbContext();
        await holderDb.Database.OpenConnectionAsync();
        try
        {
            await holderDb.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_lock({RecommendationTelemetryConstants.PERSONAL_RECOMPUTE_ADVISORY_LOCK_KEY})");

            await using ApplicationDbContext workerDb = fixture.CreateDbContext();
            var workerStore = new RecommendationPersonalizationStore(workerDb);
            PersonalRecomputeBatchResult skipped =
                await workerStore.TryRecomputeBatch(_now, 25, 120);

            skipped.Outcome.Should().Be(AnalyticsDailyRecomputeOutcome.SKIPPED);
            skipped.UsersRecomputed.Should().Be(0);
            // The skipped run opened its connection only to probe the lock.
            workerDb.Database.GetDbConnection().State.Should().Be(System.Data.ConnectionState.Closed);
        }
        finally
        {
            await holderDb.Database.ExecuteSqlRawAsync(
                $"SELECT pg_advisory_unlock({RecommendationTelemetryConstants.PERSONAL_RECOMPUTE_ADVISORY_LOCK_KEY})");
            await holderDb.Database.CloseConnectionAsync();
        }

        // After release, a separate batch completes on its own connection.
        await using ApplicationDbContext verifyDb = fixture.CreateDbContext();
        var verifyStore = new RecommendationPersonalizationStore(verifyDb);
        PersonalRecomputeBatchResult followUp =
            await verifyStore.TryRecomputeBatch(_now, 25, 120);
        followUp.Outcome.Should().Be(AnalyticsDailyRecomputeOutcome.COMPLETED);
        verifyDb.Database.GetDbConnection().State.Should().Be(System.Data.ConnectionState.Closed);
    }

    [Fact]
    public async Task TryRecomputeBatch_ShouldCloseOnlyConnectionsItOpened()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new RecommendationPersonalizationStore(db);

        await store.TryRecomputeBatch(_now, 25, 120);
        db.Database.GetDbConnection().State.Should().Be(System.Data.ConnectionState.Closed);

        await db.Database.OpenConnectionAsync();
        try
        {
            await store.TryRecomputeBatch(_now, 25, 120);
            db.Database.GetDbConnection().State.Should().Be(System.Data.ConnectionState.Open);
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    [Fact]
    public async Task GetSignals_WhenNotOptedIn_ShouldReturnNull()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User user = await SeedUser(db);
        var store = new RecommendationPersonalizationStore(db);

        PersonalAffinitySignals? signals = await store.GetSignals(user.Id, Guid.NewGuid(), [Guid.NewGuid()]);

        signals.Should().BeNull();
    }

    [Fact]
    public async Task GetSignals_WhenOptedIn_ShouldReturnPairClicksAndTagScores()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        Tag history = TestData.CreateTag("history");
        db.Tags.Add(history);
        Asset owned = AddReadyAsset(db, author, category, "Owned", tags: [history]);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset clicked = AddReadyAsset(db, author, category, "Clicked");
        Asset tagged = AddReadyAsset(db, author, category, "Tagged", tags: [history]);
        Asset plain = AddReadyAsset(db, author, category, "Plain");
        await db.SaveChangesAsync();
        AssetVersion version = await db.AssetVersions.AsNoTracking().FirstAsync(v => v.AssetId == owned.Id);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(user.Id, owned.Id, version.Id), "Owned", author.Id);
        AddClick(db, user.Id, source.Id, clicked.Id, _now.AddDays(-1));
        await db.SaveChangesAsync();

        var store = new RecommendationPersonalizationStore(db);
        await store.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await store.TryRecomputeUser(user.Id, _now)).Should().BeTrue();

        PersonalAffinitySignals? signals = await store.GetSignals(
            user.Id,
            source.Id,
            [clicked.Id, tagged.Id, plain.Id]);

        signals.Should().NotBeNull();
        signals.ClicksByTargetId.Should().ContainSingle()
            .Which.Should().Be(new KeyValuePair<Guid, long>(clicked.Id, 1));
        signals.TagScoreByCandidateId[tagged.Id].Should().Be(1);
        signals.TagScoreByCandidateId.GetValueOrDefault(plain.Id).Should().Be(0);
    }

    [Fact]
    public async Task GetSignals_WhenAnonymousEventsExist_ShouldIgnoreThem()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset target = AddReadyAsset(db, author, category, "Target");
        await db.SaveChangesAsync();
        // Visitor-only traffic carries no actor and must never attribute.
        AddClick(db, null, source.Id, target.Id, _now.AddDays(-1));
        await db.SaveChangesAsync();

        var store = new RecommendationPersonalizationStore(db);
        await store.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await store.TryRecomputeUser(user.Id, _now)).Should().BeTrue();

        (await db.UserSourceClickAffinities.CountAsync()).Should().Be(0);
        PersonalAffinitySignals? signals = await store.GetSignals(user.Id, source.Id, [target.Id]);
        signals.Should().NotBeNull();
        signals.ClicksByTargetId.Should().BeEmpty();
    }

    [Fact]
    public async Task WorkerSourceQueries_ShouldRecordPlansWithoutCheapScanClaims()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        Tag tag = TestData.CreateTag("tag");
        db.Tags.Add(tag);
        AddReadyAsset(db, author, category, "Asset", tags: [tag]);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset target = AddReadyAsset(db, author, category, "Target");
        await db.SaveChangesAsync();

        // Unrelated corpus so plans reflect selective predicates, not empty tables.
        for (var i = 0; i < 2000; i++)
        {
            db.RecommendationEvents.Add(new RecommendationEvent
            {
                Id = Guid.NewGuid(),
                EventType = i % 5 == 0 ? RecommendationEventType.CLICK : RecommendationEventType.IMPRESSION,
                OccurredAt = _now.AddDays(-(i % 80)),
                ExposureId = Guid.NewGuid(),
                SourceAssetId = Guid.NewGuid(),
                TargetAssetId = Guid.NewGuid(),
                SlotPosition = 0,
                RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
                VisitorId = Guid.NewGuid(),
                SessionId = Guid.NewGuid(),
                ActorUserId = i % 11 == 0 ? user.Id : Guid.NewGuid(),
                DeviceClass = AnalyticsDeviceClass.DESKTOP
            });
        }

        AddClick(db, user.Id, source.Id, target.Id, _now.AddDays(-3));
        await db.SaveChangesAsync();
        await db.Database.ExecuteSqlRawAsync("""ANALYZE "recommendation_events";""");
        await db.Database.ExecuteSqlRawAsync("""ANALYZE "purchases";""");
        await db.Database.ExecuteSqlRawAsync("""ANALYZE "reviews";""");

        DateTimeOffset windowStart = _now.AddDays(-RecommendationTelemetryConstants.RAW_EVENT_RETENTION_DAYS);
        Guid userId = user.Id;
        RecommendationEventType eventType = RecommendationEventType.CLICK;

        var clicksSql = db.RecommendationEvents.AsNoTracking()
            .Where(e => e.ActorUserId == userId
                && e.EventType == eventType
                && e.OccurredAt >= windowStart)
            .GroupBy(e => new { e.SourceAssetId, e.TargetAssetId })
            .Select(g => new
            {
                g.Key.SourceAssetId,
                g.Key.TargetAssetId,
                Clicks = g.LongCount(),
                LastClickedAt = g.Max(e => e.OccurredAt)
            })
            .ToQueryString();

        var purchasesSql = db.Purchases.AsNoTracking()
            .Where(p => p.UserId == userId && p.PurchasedAt >= windowStart)
            .Join(
                db.AssetTags.AsNoTracking(),
                p => p.AssetId,
                at => at.AssetId,
                (p, at) => at.TagId)
            .GroupBy(tagId => tagId)
            .Select(g => new { TagId = g.Key, Count = g.LongCount() })
            .ToQueryString();

        var reviewsSql = db.Reviews.AsNoTracking()
            .Where(r => r.UserId == userId && r.CreatedAt >= windowStart)
            .Join(
                db.AssetTags.AsNoTracking(),
                r => r.AssetId,
                at => at.AssetId,
                (r, at) => at.TagId)
            .GroupBy(tagId => tagId)
            .Select(g => new { TagId = g.Key, Count = g.LongCount() })
            .ToQueryString();

        var parameters = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            ["userid"] = userId,
            ["windowstart"] = windowStart,
            ["eventtype"] = eventType.ToString()
        };

        // No absolute plan assertions: shapes are recorded evidence, not SLA gates.
        // The clicks probe has no ActorUserId-leading index; the plan below states the cost.
        var clicksPlan = await ExplainShape(db, clicksSql, parameters);
        var purchasesPlan = await ExplainShape(db, purchasesSql, parameters);
        var reviewsPlan = await ExplainShape(db, reviewsSql, parameters);
        output.WriteLine("CLICKS-PLAN: " + clicksPlan);
        output.WriteLine("PURCHASES-PLAN: " + purchasesPlan);
        output.WriteLine("REVIEWS-PLAN: " + reviewsPlan);

        clicksPlan.Should().NotBeNullOrWhiteSpace();
        purchasesPlan.Should().NotBeNullOrWhiteSpace();
        reviewsPlan.Should().NotBeNullOrWhiteSpace();
    }

    private static async Task<string> ExplainShape(
        ApplicationDbContext db,
        string sql,
        Dictionary<string, object> parameters)
    {
        await db.Database.OpenConnectionAsync();
        try
        {
            await using var cmd = (NpgsqlCommand)db.Database.GetDbConnection().CreateCommand();
#pragma warning disable CA2100 // Measurement only: EXPLAIN over EF-generated SQL text; no user input participates.
            cmd.CommandText = "EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + sql;
#pragma warning restore CA2100
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (Match match in Regex.Matches(sql, "@[A-Za-z]\\w*"))
            {
                if (!seen.Add(match.Value))
                {
                    continue;
                }

                var key = match.Value[1..].ToLowerInvariant();
                if (!parameters.TryGetValue(key, out var value))
                {
                    throw new InvalidOperationException($"No measurement value mapped for EF parameter '{match.Value}'.");
                }

                cmd.Parameters.AddWithValue(match.Value, value);
            }

            var json = (string?)await cmd.ExecuteScalarAsync();
            json.Should().NotBeNullOrWhiteSpace();
            using var doc = JsonDocument.Parse(json);
            JsonElement plan = doc.RootElement[0].GetProperty("Plan");
            return DescribePlan(plan)
                + $" [rows={plan.GetProperty("Actual Rows").GetInt64()}"
                + $" hit={plan.GetProperty("Shared Hit Blocks").GetInt64()}"
                + $" read={plan.GetProperty("Shared Read Blocks").GetInt64()}"
                + $" execMs={doc.RootElement[0].GetProperty("Execution Time").GetDouble():0.###}]";
        }
        finally
        {
            await db.Database.CloseConnectionAsync();
        }
    }

    private static string DescribePlan(JsonElement plan)
    {
        var node = plan.GetProperty("Node Type").GetString() ?? "?";
        if (plan.TryGetProperty("Index Name", out JsonElement indexName))
        {
            node += $" using {indexName.GetString()}";
        }
        else if (plan.TryGetProperty("Relation Name", out JsonElement relation))
        {
            node += $" on {relation.GetString()}";
        }

        if (plan.TryGetProperty("Plans", out JsonElement children))
        {
            var parts = new List<string>();
            foreach (JsonElement child in children.EnumerateArray())
            {
                parts.Add(DescribePlan(child));
            }

            node += " -> (" + string.Join(" + ", parts) + ")";
        }

        return node;
    }

    private static async Task<User> SeedUser(ApplicationDbContext db, string username = "user", string email = "user@example.test")
    {
        User user = TestData.CreateUser(username, email);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user;
    }

    private static Asset AddReadyAsset(
        ApplicationDbContext db,
        User author,
        Category category,
        string title,
        IReadOnlyList<Tag>? tags = null)
    {
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title);
        db.Assets.Add(asset);
        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY));
        if (tags is { Count: > 0 })
        {
            foreach (Tag tag in tags)
            {
                db.AssetTags.Add(new AssetTag { AssetId = asset.Id, TagId = tag.Id });
            }
        }

        return asset;
    }

    private static void AddClick(
        ApplicationDbContext db,
        Guid? actorUserId,
        Guid sourceId,
        Guid targetId,
        DateTimeOffset occurredAt)
    {
        db.RecommendationEvents.Add(new RecommendationEvent
        {
            Id = Guid.NewGuid(),
            EventType = RecommendationEventType.CLICK,
            OccurredAt = occurredAt,
            ExposureId = Guid.NewGuid(),
            SourceAssetId = sourceId,
            TargetAssetId = targetId,
            SlotPosition = 0,
            RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
            VisitorId = Guid.NewGuid(),
            SessionId = Guid.NewGuid(),
            ActorUserId = actorUserId,
            DeviceClass = AnalyticsDeviceClass.DESKTOP
        });
    }
}
