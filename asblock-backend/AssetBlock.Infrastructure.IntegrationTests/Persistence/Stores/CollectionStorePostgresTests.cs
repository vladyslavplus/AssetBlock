using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class CollectionStorePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task AddItem_WhenAssetAlreadyInCollection_ShouldThrowDuplicateCollectionAssetException()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id));
        await db.SaveChangesAsync();

        var store = new CollectionStore(db);
        var collection = await store.Create(author.Id, "Picks", null);
        await store.AddItem(collection.Id, asset.Id);
        db.ChangeTracker.Clear();

        var act = () => store.AddItem(collection.Id, asset.Id);

        await act.Should().ThrowAsync<DuplicateCollectionAssetException>();
    }

    [Fact]
    public async Task CollectionItem_WhenPositionDuplicated_ShouldViolateUniqueConstraint()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "A");
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "B");
        db.Assets.AddRange(assetA, assetB);
        await db.SaveChangesAsync();
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(assetA.Id),
            TestData.CreateAssetVersion(assetB.Id));
        var collection = TestData.CreateCollection(author.Id, status: CollectionStatus.DRAFT);
        db.Collections.Add(collection);
        db.CollectionItems.Add(TestData.CreateCollectionItem(collection.Id, assetA.Id, position: 1));
        await db.SaveChangesAsync();

        db.CollectionItems.Add(TestData.CreateCollectionItem(collection.Id, assetB.Id, position: 1));
        var act = () => db.SaveChangesAsync();

        var ex = await act.Should().ThrowAsync<DbUpdateException>();
        var pg = ex.Which.InnerException.Should().BeOfType<PostgresException>().Subject;
        pg.SqlState.Should().Be(PostgresErrorCodes.UniqueViolation);
        pg.ConstraintName.Should().Be("UIX_collection_items_collection_position");
    }

    [Fact]
    public async Task GetPublicDetail_WhenSomeItemsSoftDeleted_ShouldHideThemAndRenumber()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var kept = TestData.CreateAsset(author.Id, category.Id, title: "Kept");
        var gone = TestData.CreateAsset(author.Id, category.Id, title: "Gone");
        db.Assets.AddRange(kept, gone);
        await db.SaveChangesAsync();
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(kept.Id),
            TestData.CreateAssetVersion(gone.Id));
        var collection = TestData.CreateCollection(author.Id, status: CollectionStatus.PUBLISHED);
        db.Collections.Add(collection);
        db.CollectionItems.AddRange(
            TestData.CreateCollectionItem(collection.Id, gone.Id, position: 1),
            TestData.CreateCollectionItem(collection.Id, kept.Id, position: 2));
        await db.SaveChangesAsync();

        await new AssetStore(db).SoftDelete(gone.Id, DateTimeOffset.UtcNow);

        var detail = await new CollectionStore(db).GetPublicDetail(collection.Id);

        detail.Should().NotBeNull();
        detail.Items.Should().HaveCount(1);
        detail.Items[0].AssetId.Should().Be(kept.Id);
        detail.Items[0].Position.Should().Be(1);
        detail.Items[0].Title.Should().Be("Kept");
    }

    [Fact]
    public async Task GetPublicDetail_WhenAllItemsSoftDeleted_ShouldReturnNull()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id));
        var collection = TestData.CreateCollection(author.Id, status: CollectionStatus.PUBLISHED);
        db.Collections.Add(collection);
        db.CollectionItems.Add(TestData.CreateCollectionItem(collection.Id, asset.Id, position: 1));
        await db.SaveChangesAsync();

        await new AssetStore(db).SoftDelete(asset.Id, DateTimeOffset.UtcNow);

        (await new CollectionStore(db).GetPublicDetail(collection.Id)).Should().BeNull();
    }

    [Fact]
    public async Task HardDeleteCollection_ShouldCascadeCollectionItems()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset = TestData.CreateAsset(author.Id, category.Id);
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id));
        var collection = TestData.CreateCollection(author.Id);
        db.Collections.Add(collection);
        db.CollectionItems.Add(TestData.CreateCollectionItem(collection.Id, asset.Id, position: 1));
        await db.SaveChangesAsync();

        await db.Collections.Where(c => c.Id == collection.Id).ExecuteDeleteAsync();

        (await db.CollectionItems.CountAsync(i => i.CollectionId == collection.Id)).Should().Be(0);
        (await db.Assets.CountAsync(a => a.Id == asset.Id)).Should().Be(1);
    }

    [Fact]
    public async Task RemoveItem_WhenRemovingFirstOrMiddle_ShouldKeepContiguousPositivePositions()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "Alpha");
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "Beta");
        var assetC = TestData.CreateAsset(author.Id, category.Id, title: "Gamma");
        db.Assets.AddRange(assetA, assetB, assetC);
        await db.SaveChangesAsync();
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(assetA.Id),
            TestData.CreateAssetVersion(assetB.Id),
            TestData.CreateAssetVersion(assetC.Id));
        var collection = TestData.CreateCollection(author.Id, status: CollectionStatus.DRAFT);
        db.Collections.Add(collection);
        db.CollectionItems.AddRange(
            TestData.CreateCollectionItem(collection.Id, assetA.Id, position: 1),
            TestData.CreateCollectionItem(collection.Id, assetB.Id, position: 2),
            TestData.CreateCollectionItem(collection.Id, assetC.Id, position: 3));
        await db.SaveChangesAsync();

        var store = new CollectionStore(db);

        // Remove first item → remaining B(2),C(3) renumber to 1,2
        await store.RemoveItem(collection.Id, assetA.Id);
        var afterFirst = await db.CollectionItems.AsNoTracking()
            .Where(i => i.CollectionId == collection.Id)
            .OrderBy(i => i.Position)
            .ToListAsync();
        afterFirst.Should().HaveCount(2);
        afterFirst.Should().OnlyContain(i => i.Position >= 1);
        afterFirst.Select(i => i.Position).Should().Equal(1, 2);
        afterFirst[0].AssetId.Should().Be(assetB.Id);
        afterFirst[1].AssetId.Should().Be(assetC.Id);

        // Remove middle item → remaining C renumbered to 1
        await store.RemoveItem(collection.Id, assetB.Id);
        var afterMiddle = await db.CollectionItems.AsNoTracking()
            .Where(i => i.CollectionId == collection.Id)
            .OrderBy(i => i.Position)
            .ToListAsync();
        afterMiddle.Should().ContainSingle();
        afterMiddle[0].AssetId.Should().Be(assetC.Id);
        afterMiddle[0].Position.Should().Be(1);
    }

    [Fact]
    public async Task ReorderItems_WhenNonEmpty_ShouldSucceedWithPositivePositions()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "First");
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "Second");
        var assetC = TestData.CreateAsset(author.Id, category.Id, title: "Third");
        db.Assets.AddRange(assetA, assetB, assetC);
        await db.SaveChangesAsync();
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(assetA.Id),
            TestData.CreateAssetVersion(assetB.Id),
            TestData.CreateAssetVersion(assetC.Id));
        var collection = TestData.CreateCollection(author.Id, status: CollectionStatus.DRAFT);
        db.Collections.Add(collection);
        db.CollectionItems.AddRange(
            TestData.CreateCollectionItem(collection.Id, assetA.Id, position: 1),
            TestData.CreateCollectionItem(collection.Id, assetB.Id, position: 2),
            TestData.CreateCollectionItem(collection.Id, assetC.Id, position: 3));
        await db.SaveChangesAsync();

        var store = new CollectionStore(db);
        // Reverse order: C, A, B
        await store.ReorderItems(collection.Id, [assetC.Id, assetA.Id, assetB.Id]);

        var items = await db.CollectionItems.AsNoTracking()
            .Where(i => i.CollectionId == collection.Id)
            .OrderBy(i => i.Position)
            .ToListAsync();

        items.Should().HaveCount(3);
        items.Should().OnlyContain(i => i.Position >= 1);
        items[0].AssetId.Should().Be(assetC.Id);
        items[0].Position.Should().Be(1);
        items[1].AssetId.Should().Be(assetA.Id);
        items[1].Position.Should().Be(2);
        items[2].AssetId.Should().Be(assetB.Id);
        items[2].Position.Should().Be(3);
    }

    [Fact]
    public async Task ConcurrentRemoveAndReorder_WhenParentRowIsLocked_ShouldSerializeAndStayContiguous()
    {
        await using var setup = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(setup);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "Alpha");
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "Beta");
        var assetC = TestData.CreateAsset(author.Id, category.Id, title: "Gamma");
        setup.Assets.AddRange(assetA, assetB, assetC);
        await setup.SaveChangesAsync();
        setup.AssetVersions.AddRange(
            TestData.CreateAssetVersion(assetA.Id),
            TestData.CreateAssetVersion(assetB.Id),
            TestData.CreateAssetVersion(assetC.Id));
        var collection = TestData.CreateCollection(author.Id, status: CollectionStatus.DRAFT);
        setup.Collections.Add(collection);
        setup.CollectionItems.AddRange(
            TestData.CreateCollectionItem(collection.Id, assetA.Id, position: 1),
            TestData.CreateCollectionItem(collection.Id, assetB.Id, position: 2),
            TestData.CreateCollectionItem(collection.Id, assetC.Id, position: 3));
        await setup.SaveChangesAsync();

        var firstHasLock = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowFirstCommit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        async Task RemoveFirst()
        {
            await using var db = fixture.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var store = new CollectionStore(db);
            (await store.GetForUpdate(collection.Id)).Should().NotBeNull();
            firstHasLock.SetResult();
            await allowFirstCommit.Task;
            await store.RemoveItem(collection.Id, assetA.Id);
            await transaction.CommitAsync();
        }

        async Task ReorderAfterRemove()
        {
            await using var db = fixture.CreateDbContext();
            await using var transaction = await db.Database.BeginTransactionAsync();
            var store = new CollectionStore(db);
            secondStarted.SetResult();
            (await store.GetForUpdate(collection.Id)).Should().NotBeNull();
            await store.ReorderItems(collection.Id, [assetC.Id, assetB.Id]);
            await transaction.CommitAsync();
        }

        var removeTask = RemoveFirst();
        await firstHasLock.Task;
        var reorderTask = ReorderAfterRemove();
        await secondStarted.Task;
        allowFirstCommit.SetResult();
        await Task.WhenAll(removeTask, reorderTask);

        await using var verify = fixture.CreateDbContext();
        var items = await verify.CollectionItems
            .AsNoTracking()
            .Where(i => i.CollectionId == collection.Id)
            .OrderBy(i => i.Position)
            .ToListAsync();

        items.Select(i => i.AssetId).Should().Equal(assetC.Id, assetB.Id);
        items.Select(i => i.Position).Should().Equal(1, 2);
    }
}
