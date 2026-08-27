using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Dto.Bundles;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Licenses;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class BundleStorePostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task PublishNextRevision_WhenCalledConcurrently_ShouldSerializeSequentialRevisionsWithOneCurrent()
    {
        await using var seedDb = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(seedDb);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "A", price: 10m);
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "B", price: 20m);
        seedDb.Assets.AddRange(assetA, assetB);
        await seedDb.SaveChangesAsync();
        seedDb.AssetVersions.AddRange(
            TestData.CreateAssetVersion(assetA.Id),
            TestData.CreateAssetVersion(assetB.Id));
        await seedDb.SaveChangesAsync();

        var seedStore = new BundleStore(seedDb);
        var items = CreateItems(assetA, assetB);
        (Bundle bundle, _) = await seedStore.CreateWithRevision(
            author.Id, "Race Bundle", null, 15m, "usd", 30m, items);

        var gate = new LockRaceGate(participantCount: 2);

        await using var dbA = fixture.CreateDbContext();
        await using var dbB = fixture.CreateDbContext();
        var uowA = new EfUnitOfWork(dbA);
        var uowB = new EfUnitOfWork(dbB);
        var storeA = new GatedBundleStore(new BundleStore(dbA), gate);
        var storeB = new GatedBundleStore(new BundleStore(dbB), gate);

        BundleRevision? revA = null;
        BundleRevision? revB = null;
        var taskA = uowA.ExecuteInTransaction(async ct =>
        {
            revA = await storeA.PublishNextRevision(bundle.Id, "Rev A", null, 14m, "usd", 30m, items, ct);
        });
        var taskB = uowB.ExecuteInTransaction(async ct =>
        {
            revB = await storeB.PublishNextRevision(bundle.Id, "Rev B", null, 13m, "usd", 30m, items, ct);
        });
        await Task.WhenAll(taskA, taskB);

        var revisions = new[] { revA!, revB! };
        revisions.Select(r => r.RevisionNumber).Should().BeEquivalentTo([2, 3]);

        await using var verify = fixture.CreateDbContext();
        var rows = await verify.BundleRevisions.AsNoTracking().Where(r => r.BundleId == bundle.Id).ToListAsync();
        rows.Should().HaveCount(3);
        rows.Count(r => r.IsCurrent).Should().Be(1);
        rows.Single(r => r.IsCurrent).RevisionNumber.Should().Be(3);
    }

    [Fact]
    public async Task HardDeleteAsset_ShouldSetBundleRevisionItemAssetIdNull()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "Keep", price: 10m);
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "Drop", price: 20m);
        db.Assets.AddRange(assetA, assetB);
        await db.SaveChangesAsync();
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(assetA.Id),
            TestData.CreateAssetVersion(assetB.Id));
        await db.SaveChangesAsync();

        var store = new BundleStore(db);
        (Bundle bundle, BundleRevision revision) = await store.CreateWithRevision(
            author.Id, "Nullable FK", null, 15m, "usd", 30m, CreateItems(assetA, assetB));

        await new AssetStore(db).Delete(assetB.Id);

        await using var verify = fixture.CreateDbContext();
        var items = await verify.BundleRevisionItems.AsNoTracking()
            .Where(i => i.BundleRevisionId == revision.Id)
            .OrderBy(i => i.Position)
            .ToListAsync();
        items.Should().HaveCount(2);
        items.Single(i => i.Position == 1).AssetId.Should().Be(assetA.Id);
        items.Single(i => i.Position == 2).AssetId.Should().BeNull();
        items.Single(i => i.Position == 2).AssetTitleSnapshot.Should().Be("Drop");
        (await verify.Bundles.CountAsync(b => b.Id == bundle.Id)).Should().Be(1);
    }

    [Fact]
    public async Task GetPublicDetail_WhenItemAssetSoftDeleted_ShouldReturnNull()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "A", price: 10m);
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "B", price: 20m);
        db.Assets.AddRange(assetA, assetB);
        await db.SaveChangesAsync();
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(assetA.Id),
            TestData.CreateAssetVersion(assetB.Id));
        await db.SaveChangesAsync();

        var store = new BundleStore(db);
        (Bundle bundle, _) = await store.CreateWithRevision(
            author.Id, "Unavailable", null, 15m, "usd", 30m, CreateItems(assetA, assetB));

        var before = await store.ListPublic(new ListBundlesRequest { Page = 1, PageSize = 20 });
        before.Items.Should().Contain(i => i.Id == bundle.Id);
        (await store.GetCheckoutSnapshot(bundle.Id)).Should().NotBeNull();

        await new AssetStore(db).SoftDelete(assetB.Id, DateTimeOffset.UtcNow);

        (await store.GetCheckoutSnapshot(bundle.Id)).Should().BeNull();
        var after = await store.ListPublic(new ListBundlesRequest { Page = 1, PageSize = 20 });
        after.Items.Should().NotContain(i => i.Id == bundle.Id);
    }

    [Fact]
    public async Task GetPublicDetail_ShouldReturnSemanticStringLicenseCodes()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetPersonal = TestData.CreateAsset(author.Id, category.Id, title: "Personal Asset", price: 10m);
        var assetCommercial = TestData.CreateAsset(author.Id, category.Id, title: "Commercial Asset", price: 20m);
        db.Assets.AddRange(assetPersonal, assetCommercial);
        await db.SaveChangesAsync();

        var commercialLicense = AssetLicenseCatalog.Get(AssetLicenseCode.COMMERCIAL);
        var commercialVersion = TestData.CreateAssetVersion(assetCommercial.Id);
        commercialVersion.LicenseCode = commercialLicense.Code;
        commercialVersion.LicenseTemplateVersion = commercialLicense.TemplateVersion;
        commercialVersion.LicenseDisplayName = commercialLicense.DisplayName;
        commercialVersion.LicenseTerms = commercialLicense.TermsPlainText;
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(assetPersonal.Id),
            commercialVersion);
        await db.SaveChangesAsync();

        var store = new BundleStore(db);
        (Bundle bundle, _) = await store.CreateWithRevision(
            author.Id,
            "License Codes",
            null,
            25m,
            "usd",
            30m,
            [
                new(assetPersonal.Id, 1, assetPersonal.Title, assetPersonal.Price),
                new(assetCommercial.Id, 2, assetCommercial.Title, assetCommercial.Price)
            ]);

        var detail = await store.GetPublicDetail(bundle.Id);
        detail.Should().NotBeNull();
        detail.Items.Should().HaveCount(2);
        detail.Items.Single(i => i.Position == 1).LicenseCode.Should().Be("PERSONAL");
        detail.Items.Single(i => i.Position == 2).LicenseCode.Should().Be("COMMERCIAL");

        var snapshot = await store.GetCheckoutSnapshot(bundle.Id);
        snapshot.Should().NotBeNull();
        snapshot.Items.Select(i => i.LicenseCode).Should().BeEquivalentTo(
            [AssetLicenseCode.PERSONAL, AssetLicenseCode.COMMERCIAL]);
    }

    [Fact]
    public async Task GetSellerDetail_WhenItemAssetHardDeleted_ShouldReturnNullLicenseCode()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var assetA = TestData.CreateAsset(author.Id, category.Id, title: "Keep", price: 10m);
        var assetB = TestData.CreateAsset(author.Id, category.Id, title: "Drop", price: 20m);
        db.Assets.AddRange(assetA, assetB);
        await db.SaveChangesAsync();
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(assetA.Id),
            TestData.CreateAssetVersion(assetB.Id));
        await db.SaveChangesAsync();

        var store = new BundleStore(db);
        (Bundle bundle, _) = await store.CreateWithRevision(
            author.Id, "Nullable license", null, 15m, "usd", 30m, CreateItems(assetA, assetB));

        await new AssetStore(db).Delete(assetB.Id);

        var detail = await store.GetSellerDetail(bundle.Id, author.Id);
        detail.Should().NotBeNull();
        detail.Items.Single(i => i.Position == 1).LicenseCode.Should().Be("PERSONAL");
        detail.Items.Single(i => i.Position == 2).LicenseCode.Should().BeNull();
        detail.Items.Single(i => i.Position == 2).AssetId.Should().BeNull();
    }

    [Fact]
    public async Task ListPublic_WhenFiltered_ShouldApplySellerIdMinPriceMaxPrice()
    {
        await using var db = await fixture.CreateCleanDbContext();
        var seller1 = TestData.CreateUser("seller-list-1", "seller-list-1@example.test");
        var seller2 = TestData.CreateUser("seller-list-2", "seller-list-2@example.test");
        db.Users.AddRange(seller1, seller2);
        var category = TestData.CreateCategory("filter-cat", "filter-cat");
        db.Categories.Add(category);
        await db.SaveChangesAsync();

        // seller1: cheap bundle ($5) and expensive bundle ($50)
        var cheapA = TestData.CreateAsset(seller1.Id, category.Id, title: "CheapA", price: 3m);
        var cheapB = TestData.CreateAsset(seller1.Id, category.Id, title: "CheapB", price: 4m);
        var expA   = TestData.CreateAsset(seller1.Id, category.Id, title: "ExpA", price: 30m);
        var expB   = TestData.CreateAsset(seller1.Id, category.Id, title: "ExpB", price: 40m);
        // seller2: mid bundle ($20)
        var midA   = TestData.CreateAsset(seller2.Id, category.Id, title: "MidA", price: 10m);
        var midB   = TestData.CreateAsset(seller2.Id, category.Id, title: "MidB", price: 15m);
        db.Assets.AddRange(cheapA, cheapB, expA, expB, midA, midB);
        await db.SaveChangesAsync();
        db.AssetVersions.AddRange(
            TestData.CreateAssetVersion(cheapA.Id),
            TestData.CreateAssetVersion(cheapB.Id),
            TestData.CreateAssetVersion(expA.Id),
            TestData.CreateAssetVersion(expB.Id),
            TestData.CreateAssetVersion(midA.Id),
            TestData.CreateAssetVersion(midB.Id));
        await db.SaveChangesAsync();

        var store = new BundleStore(db);
        (Bundle cheap, _) = await store.CreateWithRevision(seller1.Id, "Cheap Bundle", null, 5m, "usd", 7m,
            [new(cheapA.Id, 1, cheapA.Title, cheapA.Price), new(cheapB.Id, 2, cheapB.Title, cheapB.Price)]);
        (Bundle exp, _) = await store.CreateWithRevision(seller1.Id, "Expensive Bundle", null, 50m, "usd", 70m,
            [new(expA.Id, 1, expA.Title, expA.Price), new(expB.Id, 2, expB.Title, expB.Price)]);
        (Bundle mid, _) = await store.CreateWithRevision(seller2.Id, "Mid Bundle", null, 20m, "usd", 25m,
            [new(midA.Id, 1, midA.Title, midA.Price), new(midB.Id, 2, midB.Title, midB.Price)]);

        // Filter by SellerId → only seller1's bundles
        var bySeller = await store.ListPublic(new ListBundlesRequest { Page = 1, PageSize = 20, SellerId = seller1.Id });
        bySeller.Items.Select(i => i.Id).Should().BeEquivalentTo([cheap.Id, exp.Id]);
        bySeller.Items.Select(i => i.Id).Should().NotContain(mid.Id);

        // Filter MinPrice = 15 → should exclude cheap ($5), include exp ($50) and mid ($20)
        var byMin = await store.ListPublic(new ListBundlesRequest { Page = 1, PageSize = 20, MinPrice = 15m });
        byMin.Items.Select(i => i.Id).Should().Contain(exp.Id);
        byMin.Items.Select(i => i.Id).Should().Contain(mid.Id);
        byMin.Items.Select(i => i.Id).Should().NotContain(cheap.Id);

        // Filter MaxPrice = 25 → should exclude exp ($50), include cheap ($5) and mid ($20)
        var byMax = await store.ListPublic(new ListBundlesRequest { Page = 1, PageSize = 20, MaxPrice = 25m });
        byMax.Items.Select(i => i.Id).Should().Contain(cheap.Id);
        byMax.Items.Select(i => i.Id).Should().Contain(mid.Id);
        byMax.Items.Select(i => i.Id).Should().NotContain(exp.Id);

        // Combined MinPrice=10, MaxPrice=30 → only mid ($20) and not cheap ($5) or exp ($50)
        var byRange = await store.ListPublic(new ListBundlesRequest { Page = 1, PageSize = 20, MinPrice = 10m, MaxPrice = 30m });
        byRange.Items.Select(i => i.Id).Should().Contain(mid.Id);
        byRange.Items.Select(i => i.Id).Should().NotContain(cheap.Id);
        byRange.Items.Select(i => i.Id).Should().NotContain(exp.Id);
    }

    [Fact]
    public async Task LockAssetsInOrder_WhenAssetsExist_LocksInSingleQuery()
    {
        await using var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        var asset1 = TestData.CreateAsset(author.Id, category.Id, title: "Lock 1", price: 10m);
        var asset2 = TestData.CreateAsset(author.Id, category.Id, title: "Lock 2", price: 20m);
        db.Assets.AddRange(asset1, asset2);
        await db.SaveChangesAsync();

        var store = new BundleStore(db);
        var uow = new EfUnitOfWork(db);
        await uow.ExecuteInTransaction(async ct =>
        {
            await store.LockAssetsInOrder([asset2.Id, asset1.Id, asset2.Id], ct);
        });
    }

    private static IReadOnlyList<BundleRevisionItemDraft> CreateItems(Asset assetA, Asset assetB) =>
    [
        new(assetA.Id, 1, assetA.Title, assetA.Price),
        new(assetB.Id, 2, assetB.Title, assetB.Price)
    ];

    /// <summary>
    /// Holds both revisers at LockForUpdate until both arrive so PostgreSQL row locking is exercised.
    /// </summary>
    private sealed class LockRaceGate(int participantCount)
    {
        private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _arrived;

        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _arrived) >= participantCount)
            {
                _ready.TrySetResult();
            }

            await _ready.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
        }
    }

    private sealed class GatedBundleStore(IBundleStore inner, LockRaceGate gate) : IBundleStore
    {
        public Task<Bundle?> GetById(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetById(id, cancellationToken);

        public async Task<Bundle?> LockForUpdate(Guid id, CancellationToken cancellationToken = default)
        {
            await gate.EnterAsync(cancellationToken);
            return await inner.LockForUpdate(id, cancellationToken);
        }

        public Task<BundleDetailDto?> GetPublicDetail(Guid id, CancellationToken cancellationToken = default) =>
            inner.GetPublicDetail(id, cancellationToken);

        public Task<BundleDetailDto?> GetSellerDetail(Guid id, Guid sellerId, CancellationToken cancellationToken = default) =>
            inner.GetSellerDetail(id, sellerId, cancellationToken);

        public Task<PagedResult<BundleListItemDto>> ListPublic(ListBundlesRequest request, CancellationToken cancellationToken = default) =>
            inner.ListPublic(request, cancellationToken);

        public Task<PagedResult<BundleListItemDto>> ListForSeller(
            Guid sellerId,
            ListMyBundlesRequest request,
            CancellationToken cancellationToken = default) =>
            inner.ListForSeller(sellerId, request, cancellationToken);

        public Task<(Bundle Bundle, BundleRevision Revision)> CreateWithRevision(
            Guid sellerId,
            string title,
            string? description,
            decimal price,
            string currency,
            decimal listPriceTotal,
            IReadOnlyList<BundleRevisionItemDraft> items,
            CancellationToken cancellationToken = default) =>
            inner.CreateWithRevision(sellerId, title, description, price, currency, listPriceTotal, items, cancellationToken);

        public Task<BundleRevision> PublishNextRevision(
            Guid bundleId,
            string title,
            string? description,
            decimal price,
            string currency,
            decimal listPriceTotal,
            IReadOnlyList<BundleRevisionItemDraft> items,
            CancellationToken cancellationToken = default) =>
            inner.PublishNextRevision(bundleId, title, description, price, currency, listPriceTotal, items, cancellationToken);

        public Task<bool> TryArchive(Guid id, Guid sellerId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            inner.TryArchive(id, sellerId, now, cancellationToken);

        public Task<bool> TryRestore(Guid id, Guid sellerId, DateTimeOffset now, CancellationToken cancellationToken = default) =>
            inner.TryRestore(id, sellerId, now, cancellationToken);

        public Task<BundleCheckoutSnapshot?> GetCheckoutSnapshot(Guid bundleId, CancellationToken cancellationToken = default) =>
            inner.GetCheckoutSnapshot(bundleId, cancellationToken);

        public Task LockAssetsInOrder(IReadOnlyList<Guid> assetIds, CancellationToken cancellationToken = default) =>
            inner.LockAssetsInOrder(assetIds, cancellationToken);

        public Task<Guid?> GetPublicAnalyticsSellerId(Guid bundleId, CancellationToken cancellationToken = default) =>
            inner.GetPublicAnalyticsSellerId(bundleId, cancellationToken);
    }
}
