using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class AssetStorePostgresTests(PostgresFixture fixture)
{
    private static async Task AddWithReadyVersion(AssetStore store, Asset asset, List<Tag>? tags = null)
    {
        var version = TestData.CreateAssetVersion(asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        await store.AddWithVersion(asset, version, tags);
    }

    [Fact]
    public async Task SoftDelete_WhenAssetExists_ShouldExcludeFromGetPagedButKeepRow()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Soft-deleted listing");
        var store = new AssetStore(db);
        await AddWithReadyVersion(store, asset);

        var deletedAt = DateTimeOffset.UtcNow;
        await store.SoftDelete(asset.Id, deletedAt);

        var paged = await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10 });
        paged.Items.Should().BeEmpty();
        paged.TotalCount.Should().Be(0);

        var row = await db.Assets.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == asset.Id);
        row.DeletedAt.Should().NotBeNull();
        row.DeletedAt.Should().BeCloseTo(deletedAt, TimeSpan.FromSeconds(1));

        var fetched = await store.GetById(asset.Id);
        fetched.Should().BeNull();
    }

    [Fact]
    public async Task Update_WhenAssetIsSoftDeleted_ShouldReturnFalse()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Before delete");
        var store = new AssetStore(db);
        await store.Add(asset);
        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        var updated = await store.Update(asset.Id, title: "After delete", description: null, price: null, categoryId: null);

        updated.Should().BeFalse();
        var row = await db.Assets.IgnoreQueryFilters().AsNoTracking().SingleAsync(a => a.Id == asset.Id);
        row.Title.Should().Be("Before delete");
    }

    [Fact]
    public async Task TryAddTag_WhenTagAddedFirstTime_ShouldReturnTrue_WhenAddedSecondTime_ShouldReturnFalse()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var tag = TestData.CreateTag("atomic-tag");
        db.Tags.Add(tag);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        await store.Add(asset);

        var first = await store.TryAddTag(asset.Id, tag.Id);
        first.Should().BeTrue();

        var second = await store.TryAddTag(asset.Id, tag.Id);
        second.Should().BeFalse();

        (await store.HasAssetTag(asset.Id, tag.Id)).Should().BeTrue();
    }

    [Fact]
    public async Task TryAddTag_WhenCalledConcurrentlyAcrossTwoContexts_ShouldReturnOneTrueOneFalseAndPersistOneRow()
    {
        await using var seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var tag = TestData.CreateTag("concurrent-tag");
        seedDb.Tags.Add(tag);
        await seedDb.SaveChangesAsync();

        var store = new AssetStore(seedDb);
        await store.Add(asset);

        await using var db1 = fixture.CreateDbContext();
        await using var db2 = fixture.CreateDbContext();
        var store1 = new AssetStore(db1);
        var store2 = new AssetStore(db2);

        var task1 = store1.TryAddTag(asset.Id, tag.Id);
        var task2 = store2.TryAddTag(asset.Id, tag.Id);
        var results = await Task.WhenAll(task1, task2);

        results.Should().BeEquivalentTo([true, false]);

        await using var verifyDb = fixture.CreateDbContext();
        var count = await verifyDb.Set<AssetTag>().CountAsync(at => at.AssetId == asset.Id && at.TagId == tag.Id);
        count.Should().Be(1);
    }

    [Fact]
    public async Task TryAddTag_WhenTagDoesNotExist_ShouldPropagateForeignKeyViolation()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        await store.Add(asset);

        var act = () => store.TryAddTag(asset.Id, Guid.NewGuid());

        var ex = await act.Should().ThrowAsync<Npgsql.PostgresException>();
        ex.Which.SqlState.Should().Be(Npgsql.PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task GetOwnership_ShouldReturnAuthorIdAndIsDeleted()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        await store.Add(asset);

        var activeOwnership = await store.GetOwnership(asset.Id);
        activeOwnership.Should().NotBeNull();
        activeOwnership.AuthorId.Should().Be(author.Id);
        activeOwnership.IsDeleted.Should().BeFalse();

        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        var deletedOwnership = await store.GetOwnership(asset.Id);
        deletedOwnership.Should().NotBeNull();
        deletedOwnership.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task AddTag_WhenSamePairAddedTwice_ShouldRemainNoOpAndAllowAnotherTag()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var tagA = TestData.CreateTag("dotnet");
        var tagB = TestData.CreateTag("postgres");
        db.Tags.AddRange(tagA, tagB);
        await db.SaveChangesAsync();

        var seedStore = new AssetStore(db);
        await seedStore.Add(asset);
        await seedStore.AddTag(asset.Id, tagA.Id);

        await using var db2 = fixture.CreateDbContext();
        var store2 = new AssetStore(db2);

        var duplicate = async () => await store2.AddTag(asset.Id, tagA.Id);
        await duplicate.Should().NotThrowAsync();

        await store2.AddTag(asset.Id, tagB.Id);

        (await store2.HasAssetTag(asset.Id, tagA.Id)).Should().BeTrue();
        (await store2.HasAssetTag(asset.Id, tagB.Id)).Should().BeTrue();

        var relationCount = await db2.Set<AssetTag>().CountAsync(at => at.AssetId == asset.Id);
        relationCount.Should().Be(2);
    }

    [Fact]
    public async Task GetPaged_WhenFilteringAndSorting_ShouldReturnStablePostgresResult()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var otherCategory = TestData.CreateCategory("Scripts", "scripts");
        db.Categories.Add(otherCategory);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        var t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        await AddWithReadyVersion(store, TestData.CreateAsset(author.Id, category.Id, title: "Alpha Tool", price: 5m, createdAt: t0));
        await AddWithReadyVersion(store, TestData.CreateAsset(author.Id, category.Id, title: "Beta Tool", price: 15m, createdAt: t0.AddMinutes(1)));
        await AddWithReadyVersion(store, TestData.CreateAsset(author.Id, category.Id, title: "Gamma Pack", price: 25m, createdAt: t0.AddMinutes(2)));
        await AddWithReadyVersion(store, TestData.CreateAsset(author.Id, otherCategory.Id, title: "Other Tool", price: 1m, createdAt: t0.AddMinutes(3)));

        var page1 = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 2,
            CategoryId = category.Id,
            Search = "Tool",
            SortBy = "Title",
            SortDirection = SortDirection.ASC
        });

        page1.TotalCount.Should().Be(2);
        page1.Items.Select(a => a.Title).Should().Equal("Alpha Tool", "Beta Tool");
        page1.Page.Should().Be(1);
        page1.PageSize.Should().Be(2);

        var page2 = await store.GetPaged(new GetAssetsRequest
        {
            Page = 2,
            PageSize = 2,
            CategoryId = category.Id,
            Search = "Tool",
            SortBy = "Title",
            SortDirection = SortDirection.ASC
        });

        page2.TotalCount.Should().Be(2);
        page2.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task GetPaged_WhenTitlesTie_ShouldOrderByIdAsTieBreaker()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);

        var idLow = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var idHigh = Guid.Parse("22222222-2222-2222-2222-222222222222");
        var sharedCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        await AddWithReadyVersion(store, TestData.CreateAsset(
            author.Id, category.Id, title: "Same Title", createdAt: sharedCreatedAt, id: idHigh));
        await AddWithReadyVersion(store, TestData.CreateAsset(
            author.Id, category.Id, title: "Same Title", createdAt: sharedCreatedAt, id: idLow));

        var page = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            SortBy = "Title",
            SortDirection = SortDirection.ASC
        });

        page.Items.Select(a => a.Id).Should().Equal(idLow, idHigh);
    }

    [Fact]
    public async Task GetPaged_WhenSearchMatchesTitleCaseInsensitive_ShouldReturnAsset()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        await AddWithReadyVersion(store, TestData.CreateAsset(author.Id, category.Id, title: "Celestial Shader Pack"));

        var page = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "celestial"
        });

        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(a => a.Title == "Celestial Shader Pack");
    }

    [Fact]
    public async Task GetPaged_WhenSearchMatchesDescription_ShouldReturnAsset()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        await AddWithReadyVersion(store, TestData.CreateAsset(
            author.Id,
            category.Id,
            title: "Utility Bundle",
            description: "Includes a modular inventory system for RPG games"));

        var page = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "inventory"
        });

        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(a => a.Title == "Utility Bundle");
    }

    [Fact]
    public async Task GetPaged_WhenTypoOrPartialSearch_ShouldMatchViaTrigramOrIlike()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        await AddWithReadyVersion(store, TestData.CreateAsset(author.Id, category.Id, title: "Procedural Pack"));

        // similarity('Procedural Pack', 'Procedurl') >= 0.30 with pg_trgm
        var typo = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "Procedurl"
        });
        typo.Items.Should().ContainSingle(a => a.Title == "Procedural Pack");

        var partial = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "Procedu"
        });
        partial.Items.Should().ContainSingle(a => a.Title == "Procedural Pack");
    }

    [Fact]
    public async Task GetPaged_WhenTagsFilter_ShouldRequireAllTags()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        var tagUi = TestData.CreateTag("ui");
        var tagKit = TestData.CreateTag("kit");
        var tag3D = TestData.CreateTag("3d");
        db.Tags.AddRange(tagUi, tagKit, tag3D);
        await db.SaveChangesAsync();

        var both = TestData.CreateAsset(author.Id, category.Id, title: "UI Kit Pro");
        var onlyUi = TestData.CreateAsset(author.Id, category.Id, title: "UI Only");
        await AddWithReadyVersion(store, both, [tagUi, tagKit]);
        await AddWithReadyVersion(store, onlyUi, [tagUi]);

        var page = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Tags = ["ui", "kit"]
        });

        page.TotalCount.Should().Be(1);
        page.Items.Should().ContainSingle(a => a.Title == "UI Kit Pro");
    }

    [Fact]
    public async Task GetPaged_WhenCombinedFiltersPagingAndSort_ShouldPreserveTotalsAndOrder()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var authorA = TestData.CreateUser("author-a", "a@example.test");
        var authorB = TestData.CreateUser("author-b", "b@example.test");
        var category = TestData.CreateCategory("Audio", "audio");
        db.Users.AddRange(authorA, authorB);
        db.Categories.Add(category);
        var tagFx = TestData.CreateTag("fx");
        db.Tags.Add(tagFx);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        var t0 = DateTimeOffset.UtcNow.AddHours(-3);
        var match1 = TestData.CreateAsset(authorA.Id, category.Id, title: "FX Loop A", price: 12m, createdAt: t0, description: "cinematic fx pack");
        var match2 = TestData.CreateAsset(authorA.Id, category.Id, title: "FX Loop B", price: 18m, createdAt: t0.AddMinutes(1), description: "cinematic fx pack");
        var wrongAuthor = TestData.CreateAsset(authorB.Id, category.Id, title: "FX Loop C", price: 15m, createdAt: t0.AddMinutes(2), description: "cinematic fx pack");
        var wrongPrice = TestData.CreateAsset(authorA.Id, category.Id, title: "FX Loop D", price: 50m, createdAt: t0.AddMinutes(3), description: "cinematic fx pack");
        await AddWithReadyVersion(store, match1, [tagFx]);
        await AddWithReadyVersion(store, match2, [tagFx]);
        await AddWithReadyVersion(store, wrongAuthor, [tagFx]);
        await AddWithReadyVersion(store, wrongPrice, [tagFx]);

        var page = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 1,
            AuthorId = authorA.Id,
            CategoryId = category.Id,
            Tags = ["fx"],
            MinPrice = 10m,
            MaxPrice = 20m,
            Search = "cinematic",
            SortBy = "Title",
            SortDirection = SortDirection.ASC
        });

        page.TotalCount.Should().Be(2);
        page.Items.Should().ContainSingle();
        page.Items[0].Title.Should().Be("FX Loop A");
        page.Items[0].AuthorUsername.Should().Be("author-a");
        page.Items[0].CategoryName.Should().Be("Audio");
        page.Items[0].Tags.Should().Equal("fx");
    }

    [Fact]
    public async Task GetPaged_WhenSoftDeleted_ShouldExcludeFromSearchResults()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Hidden Nebula Asset");
        await AddWithReadyVersion(store, asset);
        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        var page = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "Nebula"
        });

        page.TotalCount.Should().Be(0);
        page.Items.Should().BeEmpty();
    }

    [Fact]
    public async Task Update_WhenTitleOrDescriptionChanges_ShouldRefreshGeneratedSearchVector()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Original Name", description: "alpha content");
        await AddWithReadyVersion(store, asset);

        (await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10, Search = "Original" }))
            .Items.Should().ContainSingle();
        (await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10, Search = "Renamed" }))
            .Items.Should().BeEmpty();

        await store.Update(asset.Id, title: "Renamed Pack", description: "omega content", price: null, categoryId: null);

        (await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10, Search = "Original" }))
            .Items.Should().BeEmpty();
        (await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10, Search = "Renamed" }))
            .Items.Should().ContainSingle(a => a.Title == "Renamed Pack");
        (await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10, Search = "omega" }))
            .Items.Should().ContainSingle(a => a.Title == "Renamed Pack");
    }

    [Fact]
    public async Task GetPaged_WhenAssetHasReviewsAndTags_ShouldProjectDtoFields()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("buyer1", "buyer1@example.test");
        db.Users.Add(buyer);
        var tagA = TestData.CreateTag("audio");
        var tagB = TestData.CreateTag("loop");
        db.Tags.AddRange(tagA, tagB);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Rated Loop Pack", price: 7.5m);
        await store.AddWithTags(asset, [tagB, tagA]);

        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, version.Id), asset.Title, author.Id);
        db.Reviews.Add(TestData.CreateReview(buyer.Id, asset.Id, rating: 4));
        await db.SaveChangesAsync();

        var page = await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10 });

        var item = page.Items.Should().ContainSingle().Subject;
        item.CategoryName.Should().Be(category.Name);
        item.AuthorUsername.Should().Be(author.Username);
        item.Tags.Should().Equal("audio", "loop");
        item.AverageRating.Should().Be(4d);
    }

    [Fact]
    public async Task ResolveDownloadAnalyticsSellerId_WhenBuyerIsEntitled_ShouldReturnAuthorIdWithoutSideEffects()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("dl-buyer", "dl-buyer@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Download Analytics Asset");
        db.Assets.Add(asset);
        var version = TestData.CreateAssetVersion(asset.Id, versionNumber: 1);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, version.Id), asset.Title, author.Id);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        var sellerId = await store.ResolveDownloadAnalyticsSellerId(
            asset.Id,
            version.Id,
            buyer.Id,
            CancellationToken.None);

        sellerId.Should().Be(author.Id);
    }

    [Fact]
    public async Task ResolveDownloadAnalyticsSellerId_WhenActorIsAuthor_ShouldReturnNull()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        var version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        var sellerId = await store.ResolveDownloadAnalyticsSellerId(
            asset.Id,
            version.Id,
            author.Id,
            CancellationToken.None);

        sellerId.Should().BeNull();
    }

    [Fact]
    public async Task GetPublicAnalyticsSellerId_WhenAssetIsSoftDeleted_ShouldReturnNull()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        await store.Add(asset);
        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        var sellerId = await store.GetPublicAnalyticsSellerId(asset.Id);

        sellerId.Should().BeNull();
    }

    [Fact]
    public async Task ResolveDownloadAnalyticsSellerId_WhenBuyerEntitledAndAssetSoftDeleted_ShouldReturnAuthorId()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("dl-buyer-deleted", "dl-buyer-deleted@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Soft-deleted Download Asset");
        db.Assets.Add(asset);
        var version = TestData.CreateAssetVersion(asset.Id, versionNumber: 1);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, version.Id), asset.Title, author.Id);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        var sellerId = await store.ResolveDownloadAnalyticsSellerId(
            asset.Id,
            version.Id,
            buyer.Id,
            CancellationToken.None);

        sellerId.Should().Be(author.Id);
        (await store.GetPublicAnalyticsSellerId(asset.Id)).Should().BeNull();
    }

    [Fact]
    public async Task ResolveDownloadAnalyticsSellerId_WhenRequestedVersionIsNotReady_ShouldReturnNull()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var buyer = TestData.CreateUser("dl-buyer-pending", "dl-buyer-pending@example.test");
        db.Users.Add(buyer);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Pending Version Download Asset");
        db.Assets.Add(asset);
        var purchased = TestData.CreateAssetVersion(asset.Id, versionNumber: 1, isCurrent: true);
        var pending = TestData.CreateAssetVersion(
            asset.Id,
            versionNumber: 2,
            isCurrent: false,
            processingStatus: AssetVersionProcessingStatus.PENDING_INSPECTION);
        db.AssetVersions.AddRange(purchased, pending);
        await db.SaveChangesAsync();
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, purchased.Id), asset.Title, author.Id);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        var sellerId = await store.ResolveDownloadAnalyticsSellerId(
            asset.Id,
            pending.Id,
            buyer.Id,
            CancellationToken.None);

        sellerId.Should().BeNull();
    }

    [Fact]
    public async Task GetById_WithMultipleTags_ShouldLoadCategoryAuthorAndAllTags()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var tag1 = TestData.CreateTag("tag-split-1");
        var tag2 = TestData.CreateTag("tag-split-2");
        db.Tags.AddRange(tag1, tag2);
        await db.SaveChangesAsync();

        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Split Query Asset");
        var store = new AssetStore(db);
        await store.AddWithTags(asset, [tag1, tag2]);

        var fetched = await store.GetById(asset.Id);
        fetched.Should().NotBeNull();
        fetched.Category.Should().NotBeNull();
        fetched.Category.Name.Should().Be(category.Name);
        fetched.Author.Should().NotBeNull();
        fetched.Author.Username.Should().Be(author.Username);
        fetched.AssetTags.Should().HaveCount(2);
        fetched.AssetTags.Select(at => at.Tag.Name).Should().BeEquivalentTo(["tag-split-1", "tag-split-2"]);
    }

    [Fact]
    public async Task GetMyListings_AndGetOwnedSellerDetail_ShouldProjectCoherentLatestVersionShape()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Seller Coherent Asset", price: 19.99m);
        db.Assets.Add(asset);
        var v1 = TestData.CreateAssetVersion(asset.Id, versionNumber: 1, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        var v2 = TestData.CreateAssetVersion(
            asset.Id,
            versionNumber: 2,
            isCurrent: false,
            processingStatus: AssetVersionProcessingStatus.PROCESSING_FAILED);
        v2.ProcessingErrorCode = "CORRUPT_ARCHIVE";
        v2.ProcessingErrorSummary = "Invalid zip structure";
        db.AssetVersions.AddRange(v1, v2);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        var listings = await store.GetMyListings(author.Id, new GetAssetsRequest { Page = 1, PageSize = 10 });
        var listing = listings.Items.Should().ContainSingle().Subject;
        listing.Id.Should().Be(asset.Id);
        listing.LatestVersionId.Should().Be(v2.Id);
        listing.LatestVersionNumber.Should().Be(2);
        listing.CurrentReadyVersionId.Should().Be(v1.Id);
        listing.LatestProcessingStatus.Should().Be(AssetVersionProcessingStatus.PROCESSING_FAILED);
        listing.LatestProcessingErrorCode.Should().Be("CORRUPT_ARCHIVE");
        listing.LatestProcessingErrorSummary.Should().Be("Invalid zip structure");

        var detail = await store.GetOwnedSellerDetail(asset.Id, author.Id);
        detail.Should().NotBeNull();
        detail.Id.Should().Be(asset.Id);
        detail.LatestVersionId.Should().Be(v2.Id);
        detail.LatestVersionNumber.Should().Be(2);
        detail.CurrentReadyVersionId.Should().Be(v1.Id);
        detail.LatestProcessingStatus.Should().Be(AssetVersionProcessingStatus.PROCESSING_FAILED);
        detail.LatestProcessingErrorCode.Should().Be("CORRUPT_ARCHIVE");
        detail.LatestProcessingErrorSummary.Should().Be("Invalid zip structure");
    }
}
