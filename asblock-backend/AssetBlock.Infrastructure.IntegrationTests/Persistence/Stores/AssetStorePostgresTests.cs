using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using AwesomeAssertions.Specialized;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class AssetStorePostgresTests(PostgresFixture fixture)
{
    private static async Task AddWithReadyVersion(AssetStore store, Asset asset, List<Tag>? tags = null)
    {
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        await store.AddWithVersion(asset, version, tags);
    }

    [Fact]
    public async Task SoftDelete_WhenAssetExists_ShouldExcludeFromGetPagedButKeepRow()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Soft-deleted listing");
        var store = new AssetStore(db);
        await AddWithReadyVersion(store, asset);

        DateTimeOffset deletedAt = DateTimeOffset.UtcNow;
        await store.SoftDelete(asset.Id, deletedAt);

        PagedResult<AssetListItem> paged = await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10 });
        paged.Items.Should().BeEmpty();
        paged.TotalCount.Should().Be(0);

        Asset row = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id);
        row.DeletedAt.Should().NotBeNull();
        row.DeletedAt.Should().BeCloseTo(deletedAt, TimeSpan.FromSeconds(1));

        Asset? fetched = await store.GetById(asset.Id);
        fetched.Should().BeNull();

        Asset? fetchedExplicitNotDeleted = await store.GetById(asset.Id, includeDeleted: false);
        fetchedExplicitNotDeleted.Should().BeNull();

        Asset? fetchedDeleted = await store.GetById(asset.Id, includeDeleted: true);
        fetchedDeleted.Should().NotBeNull();
        fetchedDeleted.Id.Should().Be(asset.Id);
    }

    [Fact]
    public async Task Update_WhenAssetIsSoftDeleted_ShouldReturnFalse()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Before delete");
        var store = new AssetStore(db);
        await store.Add(asset);
        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        var updated = await store.Update(asset.Id, title: "After delete", description: null, price: null, categoryId: null);

        updated.Should().BeFalse();
        Asset row = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id);
        row.Title.Should().Be("Before delete");
    }

    [Fact]
    public async Task TryAddTag_WhenTagAddedFirstTime_ShouldReturnTrue_WhenAddedSecondTime_ShouldReturnFalse()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        Tag tag = TestData.CreateTag("atomic-tag");
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
        await using ApplicationDbContext seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        Tag tag = TestData.CreateTag("concurrent-tag");
        seedDb.Tags.Add(tag);
        await seedDb.SaveChangesAsync();

        var store = new AssetStore(seedDb);
        await store.Add(asset);

        await using ApplicationDbContext db1 = fixture.CreateDbContext();
        await using ApplicationDbContext db2 = fixture.CreateDbContext();
        var store1 = new AssetStore(db1);
        var store2 = new AssetStore(db2);

        Task<bool> task1 = store1.TryAddTag(asset.Id, tag.Id);
        Task<bool> task2 = store2.TryAddTag(asset.Id, tag.Id);
        var results = await Task.WhenAll(task1, task2);

        results.Should().BeEquivalentTo([true, false]);

        await using ApplicationDbContext verifyDb = fixture.CreateDbContext();
        var count = await verifyDb.Set<AssetTag>().CountAsync(at => at.AssetId == asset.Id && at.TagId == tag.Id);
        count.Should().Be(1);
    }

    [Fact]
    public async Task TryAddTag_WhenTagDoesNotExist_ShouldPropagateForeignKeyViolation()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        await store.Add(asset);

        Func<Task<bool>> act = () => store.TryAddTag(asset.Id, Guid.NewGuid());

        ExceptionAssertions<PostgresException> ex = await act.Should().ThrowAsync<PostgresException>();
        ex.Which.SqlState.Should().Be(PostgresErrorCodes.ForeignKeyViolation);
    }

    [Fact]
    public async Task GetOwnership_ShouldReturnAuthorIdAndIsDeleted()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        await store.Add(asset);

        AssetOwnershipDto? activeOwnership = await store.GetOwnership(asset.Id);
        activeOwnership.Should().NotBeNull();
        activeOwnership.AuthorId.Should().Be(author.Id);
        activeOwnership.IsDeleted.Should().BeFalse();

        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        AssetOwnershipDto? deletedOwnership = await store.GetOwnership(asset.Id);
        deletedOwnership.Should().NotBeNull();
        deletedOwnership.IsDeleted.Should().BeTrue();
    }

    [Fact]
    public async Task AddTag_WhenSamePairAddedTwice_ShouldRemainNoOpAndAllowAnotherTag()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        Tag tagA = TestData.CreateTag("dotnet");
        Tag tagB = TestData.CreateTag("postgres");
        db.Tags.AddRange(tagA, tagB);
        await db.SaveChangesAsync();

        var seedStore = new AssetStore(db);
        await seedStore.Add(asset);
        await seedStore.AddTag(asset.Id, tagA.Id);

        await using ApplicationDbContext db2 = fixture.CreateDbContext();
        var store2 = new AssetStore(db2);

        Func<Task> duplicate = async () => await store2.AddTag(asset.Id, tagA.Id);
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Category otherCategory = TestData.CreateCategory("Scripts", "scripts");
        db.Categories.Add(otherCategory);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        DateTimeOffset t0 = DateTimeOffset.UtcNow.AddMinutes(-30);
        await AddWithReadyVersion(store, TestData.CreateAsset(author.Id, category.Id, title: "Alpha Tool", price: 5m, createdAt: t0));
        await AddWithReadyVersion(store, TestData.CreateAsset(author.Id, category.Id, title: "Beta Tool", price: 15m, createdAt: t0.AddMinutes(1)));
        await AddWithReadyVersion(store, TestData.CreateAsset(author.Id, category.Id, title: "Gamma Pack", price: 25m, createdAt: t0.AddMinutes(2)));
        await AddWithReadyVersion(store, TestData.CreateAsset(author.Id, otherCategory.Id, title: "Other Tool", price: 1m, createdAt: t0.AddMinutes(3)));

        PagedResult<AssetListItem> page1 = await store.GetPaged(new GetAssetsRequest
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

        PagedResult<AssetListItem> page2 = await store.GetPaged(new GetAssetsRequest
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);

        var idLow = Guid.Parse("11111111-1111-1111-1111-111111111111");
        var idHigh = Guid.Parse("22222222-2222-2222-2222-222222222222");
        DateTimeOffset sharedCreatedAt = DateTimeOffset.UtcNow.AddMinutes(-5);

        await AddWithReadyVersion(store, TestData.CreateAsset(
            author.Id, category.Id, title: "Same Title", createdAt: sharedCreatedAt, id: idHigh));
        await AddWithReadyVersion(store, TestData.CreateAsset(
            author.Id, category.Id, title: "Same Title", createdAt: sharedCreatedAt, id: idLow));

        PagedResult<AssetListItem> page = await store.GetPaged(new GetAssetsRequest
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        await AddWithReadyVersion(store, TestData.CreateAsset(author.Id, category.Id, title: "Celestial Shader Pack"));

        PagedResult<AssetListItem> page = await store.GetPaged(new GetAssetsRequest
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        await AddWithReadyVersion(store, TestData.CreateAsset(
            author.Id,
            category.Id,
            title: "Utility Bundle",
            description: "Includes a modular inventory system for RPG games"));

        PagedResult<AssetListItem> page = await store.GetPaged(new GetAssetsRequest
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        await AddWithReadyVersion(store, TestData.CreateAsset(author.Id, category.Id, title: "Procedural Pack"));

        // similarity('Procedural Pack', 'Procedurl') >= 0.30 with pg_trgm
        PagedResult<AssetListItem> typo = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "Procedurl"
        });
        typo.Items.Should().ContainSingle(a => a.Title == "Procedural Pack");

        PagedResult<AssetListItem> partial = await store.GetPaged(new GetAssetsRequest
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        Tag tagUi = TestData.CreateTag("ui");
        Tag tagKit = TestData.CreateTag("kit");
        Tag tag3D = TestData.CreateTag("3d");
        db.Tags.AddRange(tagUi, tagKit, tag3D);
        await db.SaveChangesAsync();

        Asset both = TestData.CreateAsset(author.Id, category.Id, title: "UI Kit Pro");
        Asset onlyUi = TestData.CreateAsset(author.Id, category.Id, title: "UI Only");
        await AddWithReadyVersion(store, both, [tagUi, tagKit]);
        await AddWithReadyVersion(store, onlyUi, [tagUi]);

        PagedResult<AssetListItem> page = await store.GetPaged(new GetAssetsRequest
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        User authorA = TestData.CreateUser("author-a", "a@example.test");
        User authorB = TestData.CreateUser("author-b", "b@example.test");
        Category category = TestData.CreateCategory("Audio", "audio");
        db.Users.AddRange(authorA, authorB);
        db.Categories.Add(category);
        Tag tagFx = TestData.CreateTag("fx");
        db.Tags.Add(tagFx);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        DateTimeOffset t0 = DateTimeOffset.UtcNow.AddHours(-3);
        Asset match1 = TestData.CreateAsset(authorA.Id, category.Id, title: "FX Loop A", price: 12m, createdAt: t0, description: "cinematic fx pack");
        Asset match2 = TestData.CreateAsset(authorA.Id, category.Id, title: "FX Loop B", price: 18m, createdAt: t0.AddMinutes(1), description: "cinematic fx pack");
        Asset wrongAuthor = TestData.CreateAsset(authorB.Id, category.Id, title: "FX Loop C", price: 15m, createdAt: t0.AddMinutes(2), description: "cinematic fx pack");
        Asset wrongPrice = TestData.CreateAsset(authorA.Id, category.Id, title: "FX Loop D", price: 50m, createdAt: t0.AddMinutes(3), description: "cinematic fx pack");
        await AddWithReadyVersion(store, match1, [tagFx]);
        await AddWithReadyVersion(store, match2, [tagFx]);
        await AddWithReadyVersion(store, wrongAuthor, [tagFx]);
        await AddWithReadyVersion(store, wrongPrice, [tagFx]);

        PagedResult<AssetListItem> page = await store.GetPaged(new GetAssetsRequest
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Hidden Nebula Asset");
        await AddWithReadyVersion(store, asset);
        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        PagedResult<AssetListItem> page = await store.GetPaged(new GetAssetsRequest
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Original Name", description: "alpha content");
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User buyer = TestData.CreateUser("buyer1", "buyer1@example.test");
        db.Users.Add(buyer);
        Tag tagA = TestData.CreateTag("audio");
        Tag tagB = TestData.CreateTag("loop");
        db.Tags.AddRange(tagA, tagB);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Rated Loop Pack", price: 7.5m);
        await store.AddWithTags(asset, [tagB, tagA]);

        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, version.Id), asset.Title, author.Id);
        var reviewStore = new ReviewStore(db, NullLogger<ReviewStore>.Instance);
        await reviewStore.Create(TestData.CreateReview(buyer.Id, asset.Id, rating: 4));

        PagedResult<AssetListItem> page = await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10 });

        AssetListItem item = page.Items.Should().ContainSingle().Subject;
        item.CategoryName.Should().Be(category.Name);
        item.AuthorUsername.Should().Be(author.Username);
        item.Tags.Should().Equal("audio", "loop");
        item.AverageRating.Should().Be(4d);
    }

    [Fact]
    public async Task ResolveDownloadAnalyticsSellerId_WhenBuyerIsEntitled_ShouldReturnAuthorIdWithoutSideEffects()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User buyer = TestData.CreateUser("dl-buyer", "dl-buyer@example.test");
        db.Users.Add(buyer);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Download Analytics Asset");
        db.Assets.Add(asset);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, versionNumber: 1);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, version.Id), asset.Title, author.Id);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        Guid? sellerId = await store.ResolveDownloadAnalyticsSellerId(
            asset.Id,
            version.Id,
            buyer.Id,
            CancellationToken.None);

        sellerId.Should().Be(author.Id);
    }

    [Fact]
    public async Task ResolveDownloadAnalyticsSellerId_WhenActorIsAuthor_ShouldReturnNull()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        Guid? sellerId = await store.ResolveDownloadAnalyticsSellerId(
            asset.Id,
            version.Id,
            author.Id,
            CancellationToken.None);

        sellerId.Should().BeNull();
    }

    [Fact]
    public async Task GetPublicAnalyticsSellerId_WhenAssetIsSoftDeleted_ShouldReturnNull()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id);
        var store = new AssetStore(db);
        await store.Add(asset);
        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        Guid? sellerId = await store.GetPublicAnalyticsSellerId(asset.Id);

        sellerId.Should().BeNull();
    }

    [Fact]
    public async Task ResolveDownloadAnalyticsSellerId_WhenBuyerEntitledAndAssetSoftDeleted_ShouldReturnAuthorId()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User buyer = TestData.CreateUser("dl-buyer-deleted", "dl-buyer-deleted@example.test");
        db.Users.Add(buyer);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Soft-deleted Download Asset");
        db.Assets.Add(asset);
        AssetVersion version = TestData.CreateAssetVersion(asset.Id, versionNumber: 1);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, version.Id), asset.Title, author.Id);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        await store.SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        Guid? sellerId = await store.ResolveDownloadAnalyticsSellerId(
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User buyer = TestData.CreateUser("dl-buyer-pending", "dl-buyer-pending@example.test");
        db.Users.Add(buyer);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Pending Version Download Asset");
        db.Assets.Add(asset);
        AssetVersion purchased = TestData.CreateAssetVersion(asset.Id, versionNumber: 1, isCurrent: true);
        AssetVersion pending = TestData.CreateAssetVersion(
            asset.Id,
            versionNumber: 2,
            isCurrent: false,
            processingStatus: AssetVersionProcessingStatus.PENDING_INSPECTION);
        db.AssetVersions.AddRange(purchased, pending);
        await db.SaveChangesAsync();
        TestData.AddCompletedPurchase(db, TestData.CreatePurchase(buyer.Id, asset.Id, purchased.Id), asset.Title, author.Id);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        Guid? sellerId = await store.ResolveDownloadAnalyticsSellerId(
            asset.Id,
            pending.Id,
            buyer.Id,
            CancellationToken.None);

        sellerId.Should().BeNull();
    }

    [Fact]
    public async Task GetById_WithMultipleTags_ShouldLoadCategoryAuthorAndAllTags()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Tag tag1 = TestData.CreateTag("tag-split-1");
        Tag tag2 = TestData.CreateTag("tag-split-2");
        db.Tags.AddRange(tag1, tag2);
        await db.SaveChangesAsync();

        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Split Query Asset");
        var store = new AssetStore(db);
        await store.AddWithTags(asset, [tag1, tag2]);

        Asset? fetched = await store.GetById(asset.Id);
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
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Seller Coherent Asset", price: 19.99m);
        db.Assets.Add(asset);
        AssetVersion v1 = TestData.CreateAssetVersion(asset.Id, versionNumber: 1, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        AssetVersion v2 = TestData.CreateAssetVersion(
            asset.Id,
            versionNumber: 2,
            isCurrent: false,
            processingStatus: AssetVersionProcessingStatus.PROCESSING_FAILED);
        v2.ProcessingErrorCode = "CORRUPT_ARCHIVE";
        v2.ProcessingErrorSummary = "Invalid zip structure";
        db.AssetVersions.AddRange(v1, v2);
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        PagedResult<SellerAssetListItem> listings = await store.GetMyListings(author.Id, new GetAssetsRequest { Page = 1, PageSize = 10 });
        SellerAssetListItem listing = listings.Items.Should().ContainSingle().Subject;
        listing.Id.Should().Be(asset.Id);
        listing.LatestVersionId.Should().Be(v2.Id);
        listing.LatestVersionNumber.Should().Be(2);
        listing.CurrentReadyVersionId.Should().Be(v1.Id);
        listing.LatestProcessingStatus.Should().Be(AssetVersionProcessingStatus.PROCESSING_FAILED);
        listing.LatestProcessingErrorCode.Should().Be("CORRUPT_ARCHIVE");
        listing.LatestProcessingErrorSummary.Should().Be("Invalid zip structure");

        SellerAssetDetailItem? detail = await store.GetOwnedSellerDetail(asset.Id, author.Id);
        detail.Should().NotBeNull();
        detail.Id.Should().Be(asset.Id);
        detail.LatestVersionId.Should().Be(v2.Id);
        detail.LatestVersionNumber.Should().Be(2);
        detail.CurrentReadyVersionId.Should().Be(v1.Id);
        detail.LatestProcessingStatus.Should().Be(AssetVersionProcessingStatus.PROCESSING_FAILED);
        detail.LatestProcessingErrorCode.Should().Be("CORRUPT_ARCHIVE");
        detail.LatestProcessingErrorSummary.Should().Be("Invalid zip structure");
    }

    [Fact]
    public async Task GetPaged_And_GetMyListings_ShouldProjectDenormalizedRatingAverage()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Rated Asset", price: 10m);
        asset.RatingAverage = 4.75d;
        asset.RatingCount = 12;
        var store = new AssetStore(db);
        await AddWithReadyVersion(store, asset);

        PagedResult<AssetListItem> catalog = await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10 });
        AssetListItem item = catalog.Items.Should().ContainSingle().Subject;
        item.Id.Should().Be(asset.Id);
        item.AverageRating.Should().Be(4.75d);

        PagedResult<SellerAssetListItem> myListings = await store.GetMyListings(author.Id, new GetAssetsRequest { Page = 1, PageSize = 10 });
        SellerAssetListItem sellerItem = myListings.Items.Should().ContainSingle().Subject;
        sellerItem.Id.Should().Be(asset.Id);
        sellerItem.AverageRating.Should().Be(4.75d);
    }

    [Fact]
    public async Task GetPaged_WhenSearchWithoutSort_ShouldOrderRelevanceFirstTitleOverDescriptionAndFuzzy()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        DateTimeOffset t0 = DateTimeOffset.UtcNow.AddMinutes(-30);

        Asset exactTitle = TestData.CreateAsset(author.Id, category.Id, title: "Dungeon", createdAt: t0);
        Asset partialTitle = TestData.CreateAsset(author.Id, category.Id, title: "Dungeon Props", createdAt: t0.AddMinutes(1));
        Asset descMatch = TestData.CreateAsset(author.Id, category.Id, title: "Modular Castle", description: "Contains a complete dungeon interior", createdAt: t0.AddMinutes(2));
        Asset typoMatch = TestData.CreateAsset(author.Id, category.Id, title: "Dungon Kit", createdAt: t0.AddMinutes(3));

        await AddWithReadyVersion(store, exactTitle);
        await AddWithReadyVersion(store, partialTitle);
        await AddWithReadyVersion(store, descMatch);
        await AddWithReadyVersion(store, typoMatch);

        PagedResult<AssetListItem> paged = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "Dungeon"
        });

        paged.TotalCount.Should().Be(4);
        paged.Items.Select(a => a.Title).Should().Equal(
            "Dungeon",
            "Dungeon Props",
            "Modular Castle",
            "Dungon Kit");
    }

    [Fact]
    public async Task GetPaged_WhenAssetMatchesMultipleBranches_ShouldDeduplicateAndCountOnce()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);

        Asset multiMatch = TestData.CreateAsset(
            author.Id,
            category.Id,
            title: "Knight Hero Armor",
            description: "Knight hero armor model with textures");
        await AddWithReadyVersion(store, multiMatch);

        PagedResult<AssetListItem> paged = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "Knight"
        });

        paged.TotalCount.Should().Be(1);
        paged.Items.Should().ContainSingle(a => a.Id == multiMatch.Id);
    }

    [Fact]
    public async Task GetPaged_WhenOverlappingBranchesAndPaging_ShouldReturnExactTotalCountAndFullPages()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);
        DateTimeOffset t0 = DateTimeOffset.UtcNow.AddMinutes(-50);

        Asset a1 = TestData.CreateAsset(author.Id, category.Id, title: "Space Fighter Alpha", createdAt: t0);
        Asset a2 = TestData.CreateAsset(author.Id, category.Id, title: "Space Fighter Beta", createdAt: t0.AddMinutes(1));
        Asset a3 = TestData.CreateAsset(author.Id, category.Id, title: "Space Shuttle", description: "fighter ship", createdAt: t0.AddMinutes(2));
        Asset a4 = TestData.CreateAsset(author.Id, category.Id, title: "Spce Fighter Delta", createdAt: t0.AddMinutes(3));
        Asset a5 = TestData.CreateAsset(author.Id, category.Id, title: "Deep Space Station", description: "docking for fighter", createdAt: t0.AddMinutes(4));

        await AddWithReadyVersion(store, a1);
        await AddWithReadyVersion(store, a2);
        await AddWithReadyVersion(store, a3);
        await AddWithReadyVersion(store, a4);
        await AddWithReadyVersion(store, a5);

        PagedResult<AssetListItem> p1 = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 2,
            Search = "Space Fighter"
        });

        PagedResult<AssetListItem> p2 = await store.GetPaged(new GetAssetsRequest
        {
            Page = 2,
            PageSize = 2,
            Search = "Space Fighter"
        });

        PagedResult<AssetListItem> p3 = await store.GetPaged(new GetAssetsRequest
        {
            Page = 3,
            PageSize = 2,
            Search = "Space Fighter"
        });

        p1.TotalCount.Should().Be(5);
        p1.Items.Should().HaveCount(2);

        p2.TotalCount.Should().Be(5);
        p2.Items.Should().HaveCount(2);

        p3.TotalCount.Should().Be(5);
        p3.Items.Should().HaveCount(1);

        var allFetchedIds = p1.Items.Concat(p2.Items).Concat(p3.Items).Select(x => x.Id).ToList();
        allFetchedIds.Should().HaveCount(5);
        allFetchedIds.Distinct().Should().HaveCount(5);
    }

    [Fact]
    public async Task GetPaged_WhenSearchWithExplicitSortBy_ShouldOverrideRelevanceOrder()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);

        Asset a1 = TestData.CreateAsset(author.Id, category.Id, title: "Gold Coin", price: 50m);
        Asset a2 = TestData.CreateAsset(author.Id, category.Id, title: "Gold Ingot", price: 10m);
        Asset a3 = TestData.CreateAsset(author.Id, category.Id, title: "Gold Chest", price: 30m);

        await AddWithReadyVersion(store, a1);
        await AddWithReadyVersion(store, a2);
        await AddWithReadyVersion(store, a3);

        PagedResult<AssetListItem> paged = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "Gold",
            SortBy = "Price",
            SortDirection = SortDirection.ASC
        });

        paged.TotalCount.Should().Be(3);
        paged.Items.Select(a => a.Title).Should().Equal("Gold Ingot", "Gold Chest", "Gold Coin");
    }

    [Fact]
    public async Task GetPaged_WhenSearchContainsEscapedWildcards_ShouldMatchExactCharacters()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);

        Asset percentMatch = TestData.CreateAsset(author.Id, category.Id, title: "100% Procedural");
        Asset percentNonMatch = TestData.CreateAsset(author.Id, category.Id, title: "1000 Procedural");
        Asset underscoreMatch = TestData.CreateAsset(author.Id, category.Id, title: "Shader_Pack_V1");
        Asset underscoreNonMatch = TestData.CreateAsset(author.Id, category.Id, title: "ShaderXPackXV1");

        await AddWithReadyVersion(store, percentMatch);
        await AddWithReadyVersion(store, percentNonMatch);
        await AddWithReadyVersion(store, underscoreMatch);
        await AddWithReadyVersion(store, underscoreNonMatch);

        PagedResult<AssetListItem> percentResult = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "100%"
        });
        percentResult.Items.Should().ContainSingle(a => a.Title == "100% Procedural");

        PagedResult<AssetListItem> underscoreResult = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "Pack_V1"
        });
        underscoreResult.Items.Should().ContainSingle(a => a.Title == "Shader_Pack_V1");
    }

    [Fact]
    public async Task GetPaged_WhenSearchContainsBackslashesAndWildcards_ShouldMatchLiterallyWithoutThrowing()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);

        Asset trailingSlash = TestData.CreateAsset(author.Id, category.Id, title: @"Tools\Bin\");
        Asset interiorSlash = TestData.CreateAsset(author.Id, category.Id, title: @"Shader\Core\V1");
        Asset underscore = TestData.CreateAsset(author.Id, category.Id, title: "My_Custom_Asset");
        Asset underscoreMismatch = TestData.CreateAsset(author.Id, category.Id, title: "MyXCustomXAsset");
        Asset percentExact = TestData.CreateAsset(author.Id, category.Id, title: "50% Discount Pack");
        Asset percentPartial = TestData.CreateAsset(author.Id, category.Id, title: "Special 50% Discount Pack Bundle");

        await AddWithReadyVersion(store, trailingSlash);
        await AddWithReadyVersion(store, interiorSlash);
        await AddWithReadyVersion(store, underscore);
        await AddWithReadyVersion(store, underscoreMismatch);
        await AddWithReadyVersion(store, percentExact);
        await AddWithReadyVersion(store, percentPartial);

        // Trailing backslash search: must not throw SQL escape error
        PagedResult<AssetListItem> trailingResult = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = @"Tools\Bin\"
        });
        trailingResult.Items.Should().ContainSingle(a => a.Title == @"Tools\Bin\");

        // Interior backslash search
        PagedResult<AssetListItem> interiorResult = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = @"Shader\Core\V1"
        });
        interiorResult.Items.Should().ContainSingle(a => a.Title == @"Shader\Core\V1");

        // Underscore search: should match underscore literally, not as single-character wildcard
        PagedResult<AssetListItem> underscoreResult = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "My_Custom"
        });
        underscoreResult.Items.Should().ContainSingle(a => a.Title == "My_Custom_Asset");

        // Exact-title relevance with percent: exact title "50% Discount Pack" outranks "Special 50% Discount Pack Bundle"
        PagedResult<AssetListItem> percentResult = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "50% Discount Pack"
        });
        percentResult.Items.Select(a => a.Title).Should().Equal(
            "50% Discount Pack",
            "Special 50% Discount Pack Bundle");
    }

    [Fact]
    public async Task GetPaged_WhenAssetHasNoReadyVersionOrIsSoftDeleted_ShouldEnforceVisibilityInFinalProjection()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var store = new AssetStore(db);

        Asset readyAsset = TestData.CreateAsset(author.Id, category.Id, title: "Visible Searchable Item");
        Asset pendingAsset = TestData.CreateAsset(author.Id, category.Id, title: "Pending Searchable Item");
        Asset deletedAsset = TestData.CreateAsset(author.Id, category.Id, title: "Deleted Searchable Item");

        await AddWithReadyVersion(store, readyAsset);
        await store.AddWithVersion(pendingAsset, TestData.CreateAssetVersion(pendingAsset.Id, isCurrent: false, processingStatus: AssetVersionProcessingStatus.PENDING_INSPECTION), null);
        await AddWithReadyVersion(store, deletedAsset);
        await store.SoftDelete(deletedAsset.Id, DateTimeOffset.UtcNow);

        PagedResult<AssetListItem> result = await store.GetPaged(new GetAssetsRequest
        {
            Page = 1,
            PageSize = 10,
            Search = "Searchable Item"
        });

        result.TotalCount.Should().Be(1);
        result.Items.Should().ContainSingle(a => a.Title == "Visible Searchable Item");
    }
}
