using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class AssetStorePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task SoftDelete_WhenAssetExists_ShouldExcludeFromGetPagedButKeepRow()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id, title: "Soft-deleted listing");
        var store = new AssetStore(db);
        await store.Add(asset);

        var deletedAt = DateTimeOffset.UtcNow;
        await store.SoftDelete(asset.Id, deletedAt);

        var paged = await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10 });
        paged.Items.Should().BeEmpty();
        paged.TotalCount.Should().Be(0);

        var row = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id);
        row.DeletedAt.Should().NotBeNull();
        row.DeletedAt.Should().BeCloseTo(deletedAt, TimeSpan.FromSeconds(1));
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
        var row = await db.Assets.AsNoTracking().SingleAsync(a => a.Id == asset.Id);
        row.Title.Should().Be("Before delete");
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
        await store.Add(TestData.CreateAsset(author.Id, category.Id, title: "Alpha Tool", price: 5m, createdAt: t0));
        await store.Add(TestData.CreateAsset(author.Id, category.Id, title: "Beta Tool", price: 15m, createdAt: t0.AddMinutes(1)));
        await store.Add(TestData.CreateAsset(author.Id, category.Id, title: "Gamma Pack", price: 25m, createdAt: t0.AddMinutes(2)));
        await store.Add(TestData.CreateAsset(author.Id, otherCategory.Id, title: "Other Tool", price: 1m, createdAt: t0.AddMinutes(3)));

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

        await store.Add(TestData.CreateAsset(
            author.Id, category.Id, title: "Same Title", createdAt: sharedCreatedAt, id: idHigh));
        await store.Add(TestData.CreateAsset(
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
}
