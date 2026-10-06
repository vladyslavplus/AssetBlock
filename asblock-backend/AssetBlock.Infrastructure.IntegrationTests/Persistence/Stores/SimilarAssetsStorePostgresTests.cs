using System.Security.Cryptography;
using AssetBlock.Domain.Core;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Entities;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Pgvector;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class SimilarAssetsStorePostgresTests(PostgresFixture fixture)
{
    private const string VALID_HEX_64 = "e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6";
    private const string VALID_DIGEST = "sha256:e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6";
    private const int DIMENSION = 768;

    [Fact]
    public async Task GetSimilarPublic_WhenSourceMissing_ShouldReturnNull()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(
            Guid.NewGuid(),
            6,
            SimilarAssetsQueryOptions.MetadataOnly);

        result.Should().BeNull();
    }

    [Fact]
    public async Task GetSimilarPublic_WhenSourceHiddenOrNotReady_ShouldReturnNull()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset deleted = AddReadyAsset(db, author, category, "Deleted source");
        deleted.DeletedAt = DateTimeOffset.UtcNow;
        Asset pending = TestData.CreateAsset(author.Id, category.Id, title: "Pending source");
        db.Assets.Add(pending);
        db.AssetVersions.Add(TestData.CreateAssetVersion(pending.Id, isCurrent: false, processingStatus: AssetVersionProcessingStatus.PENDING_INSPECTION));
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        (await store.GetSimilarPublic(deleted.Id, 6, SimilarAssetsQueryOptions.MetadataOnly)).Should().BeNull();
        (await store.GetSimilarPublic(pending.Id, 6, SimilarAssetsQueryOptions.MetadataOnly)).Should().BeNull();
    }

    [Fact]
    public async Task GetSimilarPublic_WhenVisibleSourceHasNoCandidates_ShouldReturnEmpty()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Category other = TestData.CreateCategory("Other", "other");
        db.Categories.Add(other);
        Asset source = AddReadyAsset(db, author, category, "Only listing");
        AddReadyAsset(db, author, other, "Other category");
        Asset deletedPeer = AddReadyAsset(db, author, category, "Deleted peer");
        deletedPeer.DeletedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(source.Id, 6, SimilarAssetsQueryOptions.MetadataOnly);

        result.Should().NotBeNull();
        result.Items.Should().BeEmpty();
        result.UsedSemanticRefinement.Should().BeFalse();
    }

    [Fact]
    public async Task GetSimilarPublic_ShouldExcludeSourceOtherCategoryPendingAndDuplicates()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Category other = TestData.CreateCategory("Audio", "audio");
        db.Categories.Add(other);
        Asset source = AddReadyAsset(db, author, category, "Source", id: Guid.Parse("00000000-0000-4000-8000-000000000001"));
        Asset sameCategory = AddReadyAsset(db, author, category, "Peer", id: Guid.Parse("00000000-0000-4000-8000-000000000002"));
        AddReadyAsset(db, author, other, "Wrong category", id: Guid.Parse("00000000-0000-4000-8000-000000000003"));
        Asset pending = TestData.CreateAsset(author.Id, category.Id, "Pending peer", id: Guid.Parse("00000000-0000-4000-8000-000000000004"));
        db.Assets.Add(pending);
        db.AssetVersions.Add(TestData.CreateAssetVersion(pending.Id, isCurrent: false, processingStatus: AssetVersionProcessingStatus.PENDING_INSPECTION));
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(source.Id, 12, SimilarAssetsQueryOptions.MetadataOnly);

        result.Should().NotBeNull();
        result.Items.Select(i => i.Id).Should().Equal(sameCategory.Id);
        result.Items.Select(i => i.Id).Should().OnlyHaveUniqueItems();
        result.Items.Should().NotContain(i => i.Id == source.Id);
        result.UsedSemanticRefinement.Should().BeFalse();
    }

    [Fact]
    public async Task GetSimilarPublic_WhenBothHaveEmptyTags_ShouldRankJaccardAsZeroThenRatingsThenId()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset lowRating = AddReadyAsset(
            db,
            author,
            category,
            "Low",
            ratingAverage: 1,
            ratingCount: 10,
            id: Guid.Parse("00000000-0000-4000-8000-000000000020"));
        Asset highRating = AddReadyAsset(
            db,
            author,
            category,
            "High",
            ratingAverage: 5,
            ratingCount: 1,
            id: Guid.Parse("00000000-0000-4000-8000-000000000021"));
        Asset tieEarlierId = AddReadyAsset(
            db,
            author,
            category,
            "Tie A",
            ratingAverage: 4,
            ratingCount: 2,
            id: Guid.Parse("00000000-0000-4000-8000-000000000010"));
        Asset tieLaterId = AddReadyAsset(
            db,
            author,
            category,
            "Tie B",
            ratingAverage: 4,
            ratingCount: 2,
            id: Guid.Parse("00000000-0000-4000-8000-000000000011"));
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(source.Id, 12, SimilarAssetsQueryOptions.MetadataOnly);

        result!.Items.Select(i => i.Id).Should().Equal(highRating.Id, tieEarlierId.Id, tieLaterId.Id, lowRating.Id);
        result.UsedSemanticRefinement.Should().BeFalse();
    }

    [Fact]
    public async Task GetSimilarPublic_ShouldRankByCanonicalTagJaccardThenRatings()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Tag alpha = TestData.CreateTag("alpha");
        Tag beta = TestData.CreateTag("beta");
        Tag gamma = TestData.CreateTag("gamma");
        db.Tags.AddRange(alpha, beta, gamma);
        Asset source = AddReadyAsset(db, author, category, "Source", tags: [alpha, beta]);
        Asset exact = AddReadyAsset(db, author, category, "Exact", ratingAverage: 1, tags: [alpha, beta]);
        Asset partial = AddReadyAsset(db, author, category, "Partial", ratingAverage: 5, tags: [alpha]);
        Asset none = AddReadyAsset(db, author, category, "None", ratingAverage: 5, tags: [gamma]);
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(source.Id, 12, SimilarAssetsQueryOptions.MetadataOnly);

        result!.Items.Select(i => i.Title).Should().Equal("Exact", "Partial", "None");
    }

    [Fact]
    public async Task GetSimilarPublic_ShouldBoundShortlistAndKeepLimitPrefix()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        for (var i = 1; i <= 105; i++)
        {
            var id = Guid.Parse($"00000000-0000-4000-8000-{i:D12}");
            AddReadyAsset(db, author, category, $"C{i:D3}", ratingAverage: i, ratingCount: i, id: id);
        }

        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? limit12 = await store.GetSimilarPublic(source.Id, 12, SimilarAssetsQueryOptions.MetadataOnly);
        SimilarPublicAssetsResult? limit6 = await store.GetSimilarPublic(source.Id, 6, SimilarAssetsQueryOptions.MetadataOnly);

        var expectedTop12 = Enumerable.Range(1, 105)
            .Select(i => (Rating: i, Id: Guid.Parse($"00000000-0000-4000-8000-{i:D12}")))
            .OrderByDescending(x => x.Rating)
            .ThenBy(x => x.Id)
            .Take(12)
            .Select(x => x.Id)
            .ToList();

        limit12!.Items.Select(i => i.Id).Should().Equal(expectedTop12);
        limit6!.Items.Select(i => i.Id).Should().Equal(expectedTop12.Take(6));
        limit12.Items.Should().NotContain(i => i.Id == Guid.Parse("00000000-0000-4000-8000-000000000001"));
        limit12.UsedSemanticRefinement.Should().BeFalse();
    }

    [Fact]
    public async Task GetSimilarPublic_WhenAllVectorsCompatible_ShouldRefineEqualJaccardByCosine()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Tag shared = TestData.CreateTag("shared");
        db.Tags.Add(shared);
        Asset source = AddReadyAsset(db, author, category, "Source", tags: [shared]);
        Asset closer = AddReadyAsset(db, author, category, "Closer", ratingAverage: 1, tags: [shared]);
        Asset farther = AddReadyAsset(db, author, category, "Farther", ratingAverage: 5, tags: [shared]);
        EmbeddingOptions embOptions = CreateEmbeddingOptions();
        var modelKey = EmbeddingModelKey.Compute(embOptions);
        AddEmbedding(db, source.Id, modelKey, embOptions, CreateUnitVector(0));
        AddEmbedding(db, closer.Id, modelKey, embOptions, CreateUnitVector(0.1f));
        AddEmbedding(db, farther.Id, modelKey, embOptions, CreateUnitVector(1.2f));
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? semantic = await store.GetSimilarPublic(
            source.Id,
            12,
            new SimilarAssetsQueryOptions(true, modelKey, DIMENSION, embOptions.ContentSchemaVersion));
        SimilarPublicAssetsResult? metadata = await store.GetSimilarPublic(
            source.Id,
            12,
            SimilarAssetsQueryOptions.MetadataOnly);

        semantic!.Items.Select(i => i.Title).Should().Equal("Closer", "Farther");
        metadata!.Items.Select(i => i.Title).Should().Equal("Farther", "Closer");
        semantic.UsedSemanticRefinement.Should().BeTrue();
        metadata.UsedSemanticRefinement.Should().BeFalse();
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("stale")]
    [InlineData("wrong-model")]
    [InlineData("zero")]
    [InlineData("schema")]
    [InlineData("dimension")]
    public async Task GetSimilarPublic_WhenAnyVectorIncompatible_ShouldUseMetadataFallback(string mode)
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Tag shared = TestData.CreateTag("shared");
        db.Tags.Add(shared);
        Asset source = AddReadyAsset(db, author, category, "Source", tags: [shared]);
        Asset closer = AddReadyAsset(db, author, category, "Closer", ratingAverage: 1, tags: [shared]);
        Asset farther = AddReadyAsset(db, author, category, "Farther", ratingAverage: 5, tags: [shared]);
        EmbeddingOptions embOptions = CreateEmbeddingOptions();
        var modelKey = EmbeddingModelKey.Compute(embOptions);
        AddEmbedding(db, source.Id, modelKey, embOptions, CreateUnitVector(0));
        AddEmbedding(db, closer.Id, modelKey, embOptions, CreateUnitVector(0.1f));

        if (mode == "stale")
        {
            AssetEmbedding embedding = AddEmbedding(db, farther.Id, modelKey, embOptions, CreateUnitVector(1.2f));
            farther.SearchRevision = 9;
            embedding.SourceRevision = 1;
        }
        else if (mode == "wrong-model")
        {
            var otherKey = SHA256.HashData("other-model"u8);
            AddEmbedding(db, farther.Id, Convert.ToHexString(otherKey).ToLowerInvariant(), embOptions, CreateUnitVector(1.2f));
        }
        else if (mode == "zero")
        {
            AddEmbedding(db, farther.Id, modelKey, embOptions, new float[DIMENSION]);
        }
        else if (mode == "schema")
        {
            AddEmbedding(db, farther.Id, modelKey, embOptions, CreateUnitVector(1.2f), contentSchemaVersion: "other-schema");
        }
        else if (mode != "missing")
        {
            AddEmbedding(db, farther.Id, modelKey, embOptions, CreateUnitVector(1.2f));
        }

        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);
        var expectedDimension = mode == "dimension" ? 767 : DIMENSION;
        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(
            source.Id,
            12,
            new SimilarAssetsQueryOptions(true, modelKey, expectedDimension, embOptions.ContentSchemaVersion));

        result!.Items.Select(i => i.Title).Should().Equal("Farther", "Closer");
        result.UsedSemanticRefinement.Should().BeFalse();
    }

    [Fact]
    public async Task GetSimilarPublic_WhenPopularityMode_ShouldRankClicksUnitsImpressionsViewsInOrder()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        // Metadata order (no tags, no ratings) is Id ascending: views, impressions, units, clicks.
        Asset views = AddReadyAsset(db, author, category, "Views", id: Guid.Parse("00000000-0000-4000-8000-000000000101"));
        Asset impressions = AddReadyAsset(db, author, category, "Impressions", id: Guid.Parse("00000000-0000-4000-8000-000000000102"));
        Asset units = AddReadyAsset(db, author, category, "Units", id: Guid.Parse("00000000-0000-4000-8000-000000000103"));
        Asset clicks = AddReadyAsset(db, author, category, "Clicks", id: Guid.Parse("00000000-0000-4000-8000-000000000104"));
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.RecommendationDaily.Add(new RecommendationDaily
        {
            DayUtc = today,
            SourceAssetId = source.Id,
            TargetAssetId = clicks.Id,
            RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
            ImpressionCount = 0,
            ClickCount = 1,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.RecommendationDaily.Add(new RecommendationDaily
        {
            DayUtc = today,
            SourceAssetId = source.Id,
            TargetAssetId = impressions.Id,
            RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
            ImpressionCount = 5,
            ClickCount = 0,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.ProductAnalyticsDaily.Add(new ProductAnalyticsDaily
        {
            SellerId = author.Id,
            DayUtc = today,
            ProductType = AnalyticsProductKind.ASSET,
            ProductId = views.Id,
            Views = 100,
            DownloadRequests = 0,
            UniqueVisitors = 40,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await SeedPurchases(db, units, buyerCount: 2, assetTitle: "Units");
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(
            source.Id,
            12,
            new SimilarAssetsQueryOptions(false, null, 0, null, UsePopularityRanking: true));

        result.Should().NotBeNull();
        result.Items.Select(i => i.Title).Should().Equal("Clicks", "Units", "Impressions", "Views");
        result.UsedSemanticRefinement.Should().BeFalse();
    }

    [Fact]
    public async Task GetSimilarPublic_WhenPopularityModeAndNoSignals_ShouldFallBackToMetadataOrder()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        AddReadyAsset(db, author, category, "Low", ratingAverage: 1, ratingCount: 10);
        AddReadyAsset(db, author, category, "High", ratingAverage: 5, ratingCount: 1);
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? popularity = await store.GetSimilarPublic(
            source.Id,
            12,
            new SimilarAssetsQueryOptions(false, null, 0, null, UsePopularityRanking: true));
        SimilarPublicAssetsResult? metadata = await store.GetSimilarPublic(
            source.Id,
            12,
            SimilarAssetsQueryOptions.MetadataOnly);

        popularity.Should().NotBeNull();
        metadata.Should().NotBeNull();
        popularity.Items.Select(i => i.Id).Should().Equal(metadata.Items.Select(i => i.Id));
        popularity.Items.Select(i => i.Title).Should().Equal("High", "Low");
        popularity.UsedSemanticRefinement.Should().BeFalse();
    }

    [Fact]
    public async Task GetSimilarPublic_WhenPopularitySignalsOutsideWindow_ShouldIgnoreThem()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset stale = AddReadyAsset(db, author, category, "Stale", id: Guid.Parse("00000000-0000-4000-8000-000000000201"));
        Asset fresh = AddReadyAsset(db, author, category, "Fresh", id: Guid.Parse("00000000-0000-4000-8000-000000000202"));
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);

        DateOnly staleDay = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(-SimilarAssetsConstants.POPULARITY_WINDOW_DAYS - 1);
        db.RecommendationDaily.Add(new RecommendationDaily
        {
            DayUtc = staleDay,
            SourceAssetId = source.Id,
            TargetAssetId = stale.Id,
            RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
            ImpressionCount = 0,
            ClickCount = 10,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        db.ProductAnalyticsDaily.Add(new ProductAnalyticsDaily
        {
            SellerId = author.Id,
            DayUtc = DateOnly.FromDateTime(DateTime.UtcNow),
            ProductType = AnalyticsProductKind.ASSET,
            ProductId = fresh.Id,
            Views = 1,
            DownloadRequests = 0,
            UniqueVisitors = 1,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(
            source.Id,
            12,
            new SimilarAssetsQueryOptions(false, null, 0, null, UsePopularityRanking: true));

        result!.Items.Select(i => i.Title).Should().Equal("Fresh", "Stale");
    }

    [Fact]
    public async Task GetSimilarPublic_WhenPopularityMode_ShouldIgnoreOtherSourceEngagementAndBundleRows()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset otherSource = AddReadyAsset(db, author, category, "Other source");
        Asset candidate = AddReadyAsset(db, author, category, "Candidate", id: Guid.Parse("00000000-0000-4000-8000-000000000301"));
        Asset plain = AddReadyAsset(db, author, category, "Plain", id: Guid.Parse("00000000-0000-4000-8000-000000000302"));
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        // Engagement recorded for another source must not leak into this source ranking.
        db.RecommendationDaily.Add(new RecommendationDaily
        {
            DayUtc = today,
            SourceAssetId = otherSource.Id,
            TargetAssetId = candidate.Id,
            RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
            ImpressionCount = 0,
            ClickCount = 9,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        // Bundle-typed product rows never count as asset views.
        db.ProductAnalyticsDaily.Add(new ProductAnalyticsDaily
        {
            SellerId = author.Id,
            DayUtc = today,
            ProductType = AnalyticsProductKind.BUNDLE,
            ProductId = candidate.Id,
            Views = 500,
            DownloadRequests = 0,
            UniqueVisitors = 200,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        // One genuine asset view for the plain candidate.
        db.ProductAnalyticsDaily.Add(new ProductAnalyticsDaily
        {
            SellerId = author.Id,
            DayUtc = today,
            ProductType = AnalyticsProductKind.ASSET,
            ProductId = plain.Id,
            Views = 1,
            DownloadRequests = 0,
            UniqueVisitors = 1,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(
            source.Id,
            12,
            new SimilarAssetsQueryOptions(false, null, 0, null, UsePopularityRanking: true));

        // Other-source clicks and bundle views are unusable: a single genuine asset view
        // outranks them, so the candidate must not lead despite 9 foreign clicks + 500 bundle views.
        result.Should().NotBeNull();
        result.Items.Should().HaveCount(3);
        result.Items[0].Title.Should().Be("Plain");
        result.Items.Should().ContainSingle(i => i.Title == "Candidate");
    }

    [Fact]
    public async Task GetSimilarPublic_WhenPopularityMode_ShouldReturnStableLimitPrefix()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset first = AddReadyAsset(db, author, category, "First");
        Asset second = AddReadyAsset(db, author, category, "Second");
        Asset third = AddReadyAsset(db, author, category, "Third");
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);

        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        db.RecommendationDaily.AddRange(
            new RecommendationDaily
            {
                DayUtc = today,
                SourceAssetId = source.Id,
                TargetAssetId = first.Id,
                RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
                ImpressionCount = 0,
                ClickCount = 3,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new RecommendationDaily
            {
                DayUtc = today,
                SourceAssetId = source.Id,
                TargetAssetId = second.Id,
                RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
                ImpressionCount = 0,
                ClickCount = 2,
                UpdatedAt = DateTimeOffset.UtcNow
            },
            new RecommendationDaily
            {
                DayUtc = today,
                SourceAssetId = source.Id,
                TargetAssetId = third.Id,
                RankingVersion = RecommendationTelemetryConstants.RANKING_VERSION_SIMILAR_METADATA,
                ImpressionCount = 0,
                ClickCount = 1,
                UpdatedAt = DateTimeOffset.UtcNow
            });
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);
        var options = new SimilarAssetsQueryOptions(false, null, 0, null, UsePopularityRanking: true);

        SimilarPublicAssetsResult? narrow = await store.GetSimilarPublic(source.Id, 2, options);
        SimilarPublicAssetsResult? wide = await store.GetSimilarPublic(source.Id, 3, options);

        narrow!.Items.Select(i => i.Title).Should().Equal("First", "Second");
        wide!.Items.Select(i => i.Title).Should().Equal("First", "Second", "Third");
    }

    [Fact]
    public async Task GetSimilarPublic_WhenMetadataMode_ShouldExposeSharedTagEvidence()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Tag alpha = TestData.CreateTag("alpha");
        Tag beta = TestData.CreateTag("beta");
        db.Tags.AddRange(alpha, beta);
        Asset source = AddReadyAsset(db, author, category, "Source", tags: [alpha, beta]);
        Asset twoTags = AddReadyAsset(db, author, category, "TwoTags", tags: [alpha, beta]);
        Asset noTags = AddReadyAsset(db, author, category, "NoTags", ratingAverage: 5);
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(
            source.Id, 12, SimilarAssetsQueryOptions.MetadataOnly);

        result.Should().NotBeNull();
        result.Evidence.Should().NotBeNull();
        result.Evidence![twoTags.Id].Should().Be(new SimilarCandidateEvidence(false, false, false, 2));
        result.Evidence[noTags.Id].Should().Be(new SimilarCandidateEvidence(false, false, false, 0));
        result.Evidence.Should().NotContainKey(source.Id);
    }

    [Fact]
    public async Task GetSimilarPublic_WhenPopularityMode_ShouldFlagSignaledCandidatesOnly()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Source");
        Asset bought = AddReadyAsset(db, author, category, "Bought");
        Asset plain = AddReadyAsset(db, author, category, "Plain");
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        await SeedPurchases(db, bought, buyerCount: 2, assetTitle: "Bought");
        var store = new AssetStore(db);
        var options = new SimilarAssetsQueryOptions(false, null, 0, null, UsePopularityRanking: true);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(source.Id, 12, options);

        result.Should().NotBeNull();
        result.Evidence![bought.Id].HasPopularitySignal.Should().BeTrue();
        result.Evidence[plain.Id].HasPopularitySignal.Should().BeFalse();
        result.Evidence[bought.Id].HasPersonalClick.Should().BeFalse();
        result.Evidence[bought.Id].HasPersonalTagScore.Should().BeFalse();
    }

    [Fact]
    public async Task GetSimilarPublic_ShouldKeepEvidenceAlignedWithLimitPrefix()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Tag alpha = TestData.CreateTag("alpha");
        db.Tags.Add(alpha);
        Asset source = AddReadyAsset(db, author, category, "Source", tags: [alpha]);
        AddReadyAsset(db, author, category, "First", ratingAverage: 5, tags: [alpha]);
        AddReadyAsset(db, author, category, "Second", ratingAverage: 4, tags: [alpha]);
        AddReadyAsset(db, author, category, "Third", ratingAverage: 3, tags: [alpha]);
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? narrow = await store.GetSimilarPublic(source.Id, 2, SimilarAssetsQueryOptions.MetadataOnly);
        SimilarPublicAssetsResult? wide = await store.GetSimilarPublic(source.Id, 4, SimilarAssetsQueryOptions.MetadataOnly);

        narrow!.Items.Select(i => i.Id).Should().Equal(wide!.Items.Select(i => i.Id).Take(2));
        narrow.Evidence!.Keys.Should().BeEquivalentTo(narrow.Items.Select(i => i.Id));
        wide.Evidence!.Keys.Should().BeEquivalentTo(wide.Items.Select(i => i.Id));
        foreach (AssetListItem item in wide.Items.Take(2))
        {
            wide.Evidence[item.Id].Should().Be(narrow.Evidence[item.Id]);
        }
    }

    [Fact]
    public async Task GetSimilarPublic_WhenCatalogTextIsMalicious_ShouldKeepExplanationsFixed()
    {
        const string payload = "<script>alert(1)</script>";
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Tag evil = TestData.CreateTag("evil\"'><img src=x onerror=alert(1)>");
        db.Tags.Add(evil);
        Asset source = AddReadyAsset(db, author, category, "Source", tags: [evil]);
        Asset peer = AddReadyAsset(db, author, category, payload, tags: [evil]);
        peer.Description = payload;
        AddReadyAsset(db, author, category, "Plain");
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(
            source.Id, 12, SimilarAssetsQueryOptions.MetadataOnly);

        result.Should().NotBeNull();
        IReadOnlyList<SimilarAssetExplanation> explanations = result.Items
            .Select(i => SimilarAssetsExplanations.Build(
                i.Id,
                result.Evidence?.GetValueOrDefault(i.Id) ?? SimilarAssetsExplanations.Fallback()))
            .ToList();
        explanations.Should().HaveCount(result.Items.Count);
        explanations.Select(e => e.AssetId).Should().Equal(result.Items.Select(i => i.Id));
        foreach (SimilarAssetExplanation explanation in explanations)
        {
            explanation.Text.Should().NotContain("<script>");
            explanation.Text.Should().NotContain("onerror");
            explanation.Text.Should().NotContain(payload);
            explanation.Text.Should().NotContain("evil");
        }

        explanations.Should().Contain(e => e.Code == SimilarAssetsExplanationCodes.SHARED_TAGS);
    }

    [Fact]
    public async Task GetSimilarPublic_WhenEmpty_ShouldReturnEmptyEvidence()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset source = AddReadyAsset(db, author, category, "Only listing");
        await db.SaveChangesAsync();
        await CatalogTestPublicationSupport.AttachTrustedPublicationForAllReadyAssets(db);
        var store = new AssetStore(db);

        SimilarPublicAssetsResult? result = await store.GetSimilarPublic(
            source.Id, 12, SimilarAssetsQueryOptions.MetadataOnly);

        result.Should().NotBeNull();
        result.Items.Should().BeEmpty();
        result.Evidence.Should().NotBeNull();
        result.Evidence!.Should().BeEmpty();
    }

    private static async Task SeedPurchases(
        ApplicationDbContext db,
        Asset asset,
        int buyerCount,
        string assetTitle)
    {
        Guid versionId = (await db.AssetVersions
            .Where(v => v.AssetId == asset.Id && v.IsCurrent)
            .Select(v => v.Id)
            .ToListAsync()).Single();
        for (var i = 0; i < buyerCount; i++)
        {
            User buyer = TestData.CreateUser($"buyer{i}", $"buyer{i}@example.test");
            db.Users.Add(buyer);
            await db.SaveChangesAsync();
            Purchase purchase = TestData.CreatePurchase(buyer.Id, asset.Id, versionId);
            TestData.AddCompletedPurchase(db, purchase, assetTitle, asset.AuthorId);
            await db.SaveChangesAsync();
        }
    }

    private static Asset AddReadyAsset(
        ApplicationDbContext db,
        User author,
        Category category,
        string title,
        double ratingAverage = 0,
        int ratingCount = 0,
        Guid? id = null,
        IReadOnlyList<Tag>? tags = null,
        AssetVersionProcessingStatus status = AssetVersionProcessingStatus.READY)
    {
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title, id: id);
        asset.RatingAverage = ratingAverage;
        asset.RatingCount = ratingCount;
        db.Assets.Add(asset);
        db.AssetVersions.Add(TestData.CreateAssetVersion(
            asset.Id,
            isCurrent: status == AssetVersionProcessingStatus.READY,
            processingStatus: status));
        if (tags is { Count: > 0 })
        {
            foreach (Tag tag in tags)
            {
                db.AssetTags.Add(new AssetTag { AssetId = asset.Id, TagId = tag.Id });
            }
        }

        return asset;
    }

    private static EmbeddingOptions CreateEmbeddingOptions() =>
        new()
        {
            Enabled = true,
            Provider = "Ollama",
            Model = "embeddinggemma:300m-qat-q8_0",
            Revision = "manifest-e84a7acc23943b7a589852cf6da122f0b925631b7884f297a001303dff54ffe6",
            Digest = VALID_DIGEST,
            Dimension = DIMENSION,
            ContentSchemaVersion = AssetPublicMetadataCanonicalizer.CONTENT_SCHEMA_VERSION
        };

    private static AssetEmbedding AddEmbedding(
        ApplicationDbContext db,
        Guid assetId,
        string modelKey,
        EmbeddingOptions options,
        float[] vector,
        string? contentSchemaVersion = null)
    {
        var embedding = new AssetEmbedding
        {
            Id = Guid.NewGuid(),
            AssetId = assetId,
            ModelKey = modelKey,
            Provider = options.Provider,
            ModelId = options.Model,
            ModelRevision = options.Revision,
            ModelDigest = options.Digest,
            Dimension = DIMENSION,
            ContentSchemaVersion = contentSchemaVersion ?? options.ContentSchemaVersion,
            SourceRevision = 1,
            ContentHash = VALID_HEX_64,
            Embedding = new Vector(vector),
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        };
        db.AssetEmbeddings.Add(embedding);
        return embedding;
    }

    private static float[] CreateUnitVector(float angle)
    {
        var vector = new float[DIMENSION];
        vector[0] = MathF.Cos(angle);
        vector[1] = MathF.Sin(angle);
        return vector;
    }
}
