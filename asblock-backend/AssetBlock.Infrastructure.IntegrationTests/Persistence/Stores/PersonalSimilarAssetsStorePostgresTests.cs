using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Recommendations;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class PersonalSimilarAssetsStorePostgresTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset _now = new(2026, 9, 10, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task GetSimilarPublic_WhenOptedInWithoutSignals_ShouldMatchPhaseAOrder()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Tag shared = TestData.CreateTag("shared");
        db.Tags.Add(shared);
        Asset source = AddReadyAsset(db, author, category, "Source", tags: [shared]);
        AddReadyAsset(db, author, category, "First", ratingAverage: 5, tags: [shared]);
        AddReadyAsset(db, author, category, "Second", ratingAverage: 1, tags: [shared]);
        await db.SaveChangesAsync();
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();

        var personalization = new RecommendationPersonalizationStore(db);
        await personalization.SetPersonalized(user.Id, true, _now.AddDays(-1));
        var store = new AssetStore(db, personalizationStore: personalization);
        var options = new SimilarAssetsQueryOptions(false, null, 0, null, PersonalUserId: user.Id);

        SimilarPublicAssetsResult? personal = await store.GetSimilarPublic(source.Id, 12, options);
        SimilarPublicAssetsResult? phaseA = await store.GetSimilarPublic(source.Id, 12, SimilarAssetsQueryOptions.MetadataOnly);

        personal.Should().NotBeNull();
        personal.UsedPersonalization.Should().BeTrue();
        personal.Items.Select(i => i.Title).Should().Equal("First", "Second");
        personal.Items.Select(i => i.Id).Should().Equal(phaseA!.Items.Select(i => i.Id));
    }

    [Fact]
    public async Task GetSimilarPublic_ShouldOrderBySameSourceClicksFirst()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset more = AddReadyAsset(db, author, category, "More");
        Asset less = AddReadyAsset(db, author, category, "Less");
        Asset foreign = AddReadyAsset(db, author, category, "Foreign");
        await db.SaveChangesAsync();
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        AddClick(db, user.Id, source.Id, more.Id, _now.AddDays(-2));
        AddClick(db, user.Id, source.Id, more.Id, _now.AddDays(-1));
        AddClick(db, user.Id, source.Id, less.Id, _now.AddDays(-1));
        // Clicks on another source must not leak into this source's ranking.
        AddClick(db, user.Id, Guid.NewGuid(), foreign.Id, _now.AddDays(-1));
        AddClick(db, user.Id, Guid.NewGuid(), foreign.Id, _now.AddDays(-1));
        AddClick(db, user.Id, Guid.NewGuid(), foreign.Id, _now.AddDays(-1));
        await db.SaveChangesAsync();

        var personalization = new RecommendationPersonalizationStore(db);
        await personalization.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await personalization.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        var store = new AssetStore(db, personalizationStore: personalization);
        var options = new SimilarAssetsQueryOptions(false, null, 0, null, PersonalUserId: user.Id);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(source.Id, 12, options);

        result.Should().NotBeNull();
        result.UsedPersonalization.Should().BeTrue();
        result.Items.Select(i => i.Title).Should().Equal("More", "Less", "Foreign");
    }

    [Fact]
    public async Task GetSimilarPublic_ShouldOrderByTagAffinityOverMetadata()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Tag sourceTag = TestData.CreateTag("sourcetag");
        Tag historyTag = TestData.CreateTag("historytag");
        db.Tags.AddRange(sourceTag, historyTag);
        // Metadata order prefers MetadataMatch (shares the source tag); personal history prefers HistoryMatch.
        Asset source = AddReadyAsset(db, author, category, "Source", tags: [sourceTag]);
        AddReadyAsset(db, author, category, "MetadataMatch", tags: [sourceTag]);
        AddReadyAsset(db, author, category, "HistoryMatch", ratingAverage: 1, tags: [historyTag]);
        Asset historySource = AddReadyAsset(db, author, category, "HistorySource", tags: [historyTag]);
        await db.SaveChangesAsync();
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        AssetVersion version = await db.AssetVersions.AsNoTracking().FirstAsync(v => v.AssetId == historySource.Id);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(user.Id, historySource.Id, version.Id), "HistorySource", author.Id);
        await db.SaveChangesAsync();

        var personalization = new RecommendationPersonalizationStore(db);
        await personalization.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await personalization.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        AssetStore store = new(db, personalizationStore: personalization);
        SimilarAssetsQueryOptions metadataOptions = SimilarAssetsQueryOptions.MetadataOnly;
        var personalOptions = new SimilarAssetsQueryOptions(false, null, 0, null, PersonalUserId: user.Id);

        SimilarPublicAssetsResult? metadata = await store.GetSimilarPublic(source.Id, 12, metadataOptions);
        SimilarPublicAssetsResult? personal = await store.GetSimilarPublic(source.Id, 12, personalOptions);

        metadata!.Items.Select(i => i.Title).Should().Equal("MetadataMatch", "HistoryMatch", "HistorySource");
        personal.Should().NotBeNull();
        personal.UsedPersonalization.Should().BeTrue();
        personal.Items.Select(i => i.Title).Should().Equal("HistoryMatch", "HistorySource", "MetadataMatch");
    }

    [Fact]
    public async Task GetSimilarPublic_ShouldCountEachSharedTagOnceAndBreakTiesByPhaseA()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Tag tagA = TestData.CreateTag("taga");
        Tag tagB = TestData.CreateTag("tagb");
        db.Tags.AddRange(tagA, tagB);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset twoTags = AddReadyAsset(db, author, category, "TwoTags", tags: [tagA, tagB]);
        Asset oneTag = AddReadyAsset(db, author, category, "OneTag", ratingAverage: 5, tags: [tagA]);
        Asset historyA = AddReadyAsset(db, author, category, "HistoryA", ratingAverage: 1, tags: [tagA]);
        Asset historyB = AddReadyAsset(db, author, category, "HistoryB", tags: [tagB]);
        await db.SaveChangesAsync();
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        Dictionary<Guid, Guid> versionIds = await db.AssetVersions.AsNoTracking()
            .Where(v => v.IsCurrent && (v.AssetId == historyA.Id || v.AssetId == historyB.Id))
            .ToDictionaryAsync(v => v.AssetId, v => v.Id);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(user.Id, historyA.Id, versionIds[historyA.Id]), "HistoryA", author.Id);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(user.Id, historyB.Id, versionIds[historyB.Id]), "HistoryB", author.Id);
        await db.SaveChangesAsync();

        var personalization = new RecommendationPersonalizationStore(db);
        await personalization.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await personalization.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        PersonalAffinitySignals? signals = await personalization.GetSignals(
            user.Id,
            source.Id,
            [twoTags.Id, oneTag.Id, historyA.Id, historyB.Id]);
        // Each shared tag counts exactly once: 1 purchase on tagA + 1 on tagB = 2, not multiplied.
        signals.Should().NotBeNull();
        signals.TagScoreByCandidateId[twoTags.Id].Should().Be(2);
        signals.TagScoreByCandidateId[oneTag.Id].Should().Be(1);

        var store = new AssetStore(db, personalizationStore: personalization);
        var options = new SimilarAssetsQueryOptions(false, null, 0, null, PersonalUserId: user.Id);
        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(source.Id, 12, options);

        // HistoryA/HistoryB carry tag score 1 each and tie with OneTag; Phase A keys
        // (Jaccard, then ratings) break the tie deterministically.
        result!.Items.Select(i => i.Title).Should().Equal("TwoTags", "OneTag", "HistoryA", "HistoryB");
    }

    [Fact]
    public async Task GetSimilarPublic_WhenOptedOut_ShouldUsePhaseAOrderWithoutPersonalFlag()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        AddReadyAsset(db, author, category, "First", ratingAverage: 5);
        Asset second = AddReadyAsset(db, author, category, "Second", ratingAverage: 1);
        await db.SaveChangesAsync();
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        AddClick(db, user.Id, source.Id, second.Id, _now.AddDays(-1));
        await db.SaveChangesAsync();

        var personalization = new RecommendationPersonalizationStore(db);
        await personalization.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await personalization.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        // Withdraw: affinity gone, flag off.
        await personalization.SetPersonalized(user.Id, false, _now);
        var store = new AssetStore(db, personalizationStore: personalization);
        var options = new SimilarAssetsQueryOptions(false, null, 0, null, PersonalUserId: user.Id);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(source.Id, 12, options);

        result.Should().NotBeNull();
        result.UsedPersonalization.Should().BeFalse();
        result.Items.Select(i => i.Title).Should().Equal("First", "Second");
    }

    [Fact]
    public async Task GetSimilarPublic_ShouldIsolateAccountsFromEachOther()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset alpha = AddReadyAsset(db, author, category, "Alpha");
        Asset beta = AddReadyAsset(db, author, category, "Beta");
        await db.SaveChangesAsync();
        User first = TestData.CreateUser("first", "first@example.test");
        User second = TestData.CreateUser("second", "second@example.test");
        db.Users.AddRange(first, second);
        await db.SaveChangesAsync();
        AddClick(db, first.Id, source.Id, alpha.Id, _now.AddDays(-1));
        AddClick(db, second.Id, source.Id, beta.Id, _now.AddDays(-1));
        await db.SaveChangesAsync();

        var personalization = new RecommendationPersonalizationStore(db);
        await personalization.SetPersonalized(first.Id, true, _now.AddDays(-10));
        await personalization.SetPersonalized(second.Id, true, _now.AddDays(-10));
        (await personalization.TryRecomputeUser(first.Id, _now)).Should().BeTrue();
        (await personalization.TryRecomputeUser(second.Id, _now)).Should().BeTrue();
        var store = new AssetStore(db, personalizationStore: personalization);

        SimilarPublicAssetsResult? forFirst = await store.GetSimilarPublic(
            source.Id, 12, new SimilarAssetsQueryOptions(false, null, 0, null, PersonalUserId: first.Id));
        SimilarPublicAssetsResult? forSecond = await store.GetSimilarPublic(
            source.Id, 12, new SimilarAssetsQueryOptions(false, null, 0, null, PersonalUserId: second.Id));

        forFirst!.Items.Select(i => i.Title).First().Should().Be("Alpha");
        forSecond!.Items.Select(i => i.Title).First().Should().Be("Beta");
    }

    [Fact]
    public async Task GetSimilarPublic_WhenAnonymousClicksExist_ShouldIgnoreThem()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset clicked = AddReadyAsset(db, author, category, "Clicked", ratingAverage: 1);
        AddReadyAsset(db, author, category, "Rated", ratingAverage: 5);
        await db.SaveChangesAsync();
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        AddClick(db, null, source.Id, clicked.Id, _now.AddDays(-1));
        await db.SaveChangesAsync();

        var personalization = new RecommendationPersonalizationStore(db);
        await personalization.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await personalization.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        var store = new AssetStore(db, personalizationStore: personalization);
        var options = new SimilarAssetsQueryOptions(false, null, 0, null, PersonalUserId: user.Id);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(source.Id, 12, options);

        // Visitor-only traffic never attributes: metadata order stands.
        result!.Items.Select(i => i.Title).Should().Equal("Rated", "Clicked");
    }

    [Fact]
    public async Task GetSimilarPublic_WhenPopularityMode_ShouldKeepLimitPrefixWithPersonalRerank()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset personal = AddReadyAsset(db, author, category, "Personal");
        AddReadyAsset(db, author, category, "Other");
        await db.SaveChangesAsync();
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        AddClick(db, user.Id, source.Id, personal.Id, _now.AddDays(-1));
        await db.SaveChangesAsync();

        var personalization = new RecommendationPersonalizationStore(db);
        await personalization.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await personalization.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        var store = new AssetStore(db, personalizationStore: personalization);
        var options = new SimilarAssetsQueryOptions(false, null, 0, null, UsePopularityRanking: true, PersonalUserId: user.Id);

        SimilarPublicAssetsResult? narrow = await store.GetSimilarPublic(source.Id, 1, options);
        SimilarPublicAssetsResult? wide = await store.GetSimilarPublic(source.Id, 2, options);

        narrow!.Items.Select(i => i.Title).Should().Equal("Personal");
        wide!.Items.Select(i => i.Title).Should().Equal("Personal", "Other");
        wide.UsedPersonalization.Should().BeTrue();
    }

    [Fact]
    public async Task GetSimilarPublic_WhenPersonalClick_ShouldExplainWithRecommendationChoices()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset clicked = AddReadyAsset(db, author, category, "Clicked");
        await db.SaveChangesAsync();
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        AddClick(db, user.Id, source.Id, clicked.Id, _now.AddDays(-1));
        await db.SaveChangesAsync();

        var personalization = new RecommendationPersonalizationStore(db);
        await personalization.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await personalization.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        var store = new AssetStore(db, personalizationStore: personalization);
        var options = new SimilarAssetsQueryOptions(false, null, 0, null, PersonalUserId: user.Id);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(source.Id, 12, options);

        result.Should().NotBeNull();
        result.UsedPersonalization.Should().BeTrue();
        result.Evidence![clicked.Id].HasPersonalClick.Should().BeTrue();
        SimilarAssetExplanation explanation = SimilarAssetsExplanations.Build(
            clicked.Id, result.Evidence[clicked.Id]);
        explanation.Code.Should().Be(SimilarAssetsExplanationCodes.PERSONAL_RECOMMENDATION_CHOICES);
    }

    [Fact]
    public async Task GetSimilarPublic_WhenPersonalTagScore_ShouldExplainWithTagInterests()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Tag historyTag = TestData.CreateTag("historytag");
        db.Tags.Add(historyTag);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset tagged = AddReadyAsset(db, author, category, "Tagged", tags: [historyTag]);
        Asset historySource = AddReadyAsset(db, author, category, "HistorySource", tags: [historyTag]);
        await db.SaveChangesAsync();
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        AssetVersion version = await db.AssetVersions.AsNoTracking().FirstAsync(v => v.AssetId == historySource.Id);
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(user.Id, historySource.Id, version.Id), "HistorySource", author.Id);
        await db.SaveChangesAsync();

        var personalization = new RecommendationPersonalizationStore(db);
        await personalization.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await personalization.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        var store = new AssetStore(db, personalizationStore: personalization);
        var options = new SimilarAssetsQueryOptions(false, null, 0, null, PersonalUserId: user.Id);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(source.Id, 12, options);

        result.Should().NotBeNull();
        result.UsedPersonalization.Should().BeTrue();
        result.Evidence![tagged.Id].HasPersonalClick.Should().BeFalse();
        result.Evidence[tagged.Id].HasPersonalTagScore.Should().BeTrue();
        SimilarAssetExplanation explanation = SimilarAssetsExplanations.Build(
            tagged.Id, result.Evidence[tagged.Id]);
        explanation.Code.Should().Be(SimilarAssetsExplanationCodes.PERSONAL_TAG_INTERESTS);
    }

    [Fact]
    public async Task GetSimilarPublic_WhenOptedOut_ShouldCarryNoPersonalEvidence()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset peer = AddReadyAsset(db, author, category, "Peer");
        await db.SaveChangesAsync();
        User user = TestData.CreateUser("buyer", "buyer@example.test");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        AddClick(db, user.Id, source.Id, peer.Id, _now.AddDays(-1));
        await db.SaveChangesAsync();

        var personalization = new RecommendationPersonalizationStore(db);
        await personalization.SetPersonalized(user.Id, true, _now.AddDays(-10));
        (await personalization.TryRecomputeUser(user.Id, _now)).Should().BeTrue();
        await personalization.SetPersonalized(user.Id, false, _now);
        var store = new AssetStore(db, personalizationStore: personalization);
        var options = new SimilarAssetsQueryOptions(false, null, 0, null, PersonalUserId: user.Id);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(source.Id, 12, options);

        result.Should().NotBeNull();
        result.UsedPersonalization.Should().BeFalse();
        result.Evidence!.Values.Should().OnlyContain(e =>
            !e.HasPersonalClick && !e.HasPersonalTagScore);
    }

    private static Asset AddReadyAsset(
        ApplicationDbContext db,
        User author,
        Category category,
        string title,
        double ratingAverage = 0,
        IReadOnlyList<Tag>? tags = null)
    {
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title);
        asset.RatingAverage = ratingAverage;
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
