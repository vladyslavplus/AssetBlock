using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class AssetCatalogVisibilityPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task PublicGetPaged_WithAuthorIdFilter_MustNotExposePendingOrRejectedAssets()
    {
        ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);

        // 1. Asset A: Has a READY current version
        Asset readyAsset = TestData.CreateAsset(author.Id, category.Id, title: "Ready Public Asset");
        db.Assets.Add(readyAsset);
        await db.SaveChangesAsync();
        AssetVersion readyVersion = TestData.CreateAssetVersion(readyAsset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        db.AssetVersions.Add(readyVersion);

        // 2. Asset B: In PENDING_INSPECTION state (not yet published)
        Asset pendingAsset = TestData.CreateAsset(author.Id, category.Id, title: "Pending Quarantine Asset");
        db.Assets.Add(pendingAsset);
        await db.SaveChangesAsync();
        AssetVersion pendingVersion = TestData.CreateAssetVersion(pendingAsset.Id, isCurrent: false, processingStatus: AssetVersionProcessingStatus.PENDING_INSPECTION);
        db.AssetVersions.Add(pendingVersion);

        // 3. Asset C: In REJECTED state
        Asset rejectedAsset = TestData.CreateAsset(author.Id, category.Id, title: "Rejected Malware Asset");
        db.Assets.Add(rejectedAsset);
        await db.SaveChangesAsync();
        AssetVersion rejectedVersion = TestData.CreateAssetVersion(rejectedAsset.Id, isCurrent: false, processingStatus: AssetVersionProcessingStatus.REJECTED);
        rejectedVersion.ProcessingErrorCode = "MALWARE_DETECTED";
        rejectedVersion.ProcessingErrorSummary = "Malicious content detected in upload.";
        db.AssetVersions.Add(rejectedVersion);

        await db.SaveChangesAsync();

        var assetStore = new AssetStore(db);

        // Act 1: Public catalog query WITH authorId filter (unauthenticated public browser browsing seller profile)
        PagedResult<AssetListItem> publicResult = await assetStore.GetPaged(new GetAssetsRequest
        {
            AuthorId = author.Id,
            Page = 1,
            PageSize = 50
        });

        // Assert 1: Public query only returns the READY asset
        publicResult.Items.Should().HaveCount(1);
        publicResult.Items[0].Id.Should().Be(readyAsset.Id);
        publicResult.Items[0].Title.Should().Be("Ready Public Asset");

        // Act 2: Authenticated seller management dashboard query (GetMyListings)
        PagedResult<SellerAssetListItem> ownerResult = await assetStore.GetMyListings(author.Id, new GetAssetsRequest
        {
            Page = 1,
            PageSize = 50
        });

        // Assert 2: Owner sees all 3 assets (Ready, Pending, and Rejected) with latest processing state.
        ownerResult.Items.Should().HaveCount(3);
        ownerResult.Items.Select(i => i.Id).Should().BeEquivalentTo([readyAsset.Id, pendingAsset.Id, rejectedAsset.Id]);

        SellerAssetListItem readyRow = ownerResult.Items.Single(i => i.Id == readyAsset.Id);
        readyRow.LatestProcessingStatus.Should().Be(AssetVersionProcessingStatus.READY);
        readyRow.CurrentReadyVersionId.Should().Be(readyVersion.Id);
        readyRow.LatestVersionId.Should().Be(readyVersion.Id);

        SellerAssetListItem pendingRow = ownerResult.Items.Single(i => i.Id == pendingAsset.Id);
        pendingRow.LatestProcessingStatus.Should().Be(AssetVersionProcessingStatus.PENDING_INSPECTION);
        pendingRow.CurrentReadyVersionId.Should().BeNull();
        pendingRow.LatestVersionId.Should().Be(pendingVersion.Id);

        SellerAssetListItem rejectedRow = ownerResult.Items.Single(i => i.Id == rejectedAsset.Id);
        rejectedRow.LatestProcessingStatus.Should().Be(AssetVersionProcessingStatus.REJECTED);
        rejectedRow.CurrentReadyVersionId.Should().BeNull();
        rejectedRow.LatestProcessingErrorCode.Should().Be("MALWARE_DETECTED");
    }

    [Fact]
    public async Task GetOwnedSellerDetail_WhenPendingOwned_ShouldReturnProcessingState()
    {
        ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset pendingAsset = TestData.CreateAsset(author.Id, category.Id, title: "Pending Quarantine Asset");
        db.Assets.Add(pendingAsset);
        await db.SaveChangesAsync();
        AssetVersion pendingVersion = TestData.CreateAssetVersion(
            pendingAsset.Id,
            isCurrent: false,
            processingStatus: AssetVersionProcessingStatus.PENDING_INSPECTION);
        db.AssetVersions.Add(pendingVersion);
        await db.SaveChangesAsync();

        var assetStore = new AssetStore(db);
        SellerAssetDetailItem? detail = await assetStore.GetOwnedSellerDetail(pendingAsset.Id, author.Id);

        detail.Should().NotBeNull();
        detail.LatestProcessingStatus.Should().Be(AssetVersionProcessingStatus.PENDING_INSPECTION);
        detail.CurrentReadyVersionId.Should().BeNull();
        detail.LatestVersionId.Should().Be(pendingVersion.Id);
        detail.Title.Should().Be("Pending Quarantine Asset");
    }

    [Fact]
    public async Task GetOwnedSellerDetail_WhenForeignOrMissing_ShouldReturnNull()
    {
        ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        User stranger = TestData.CreateUser("stranger", "stranger@example.test");
        db.Users.Add(stranger);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Owned");
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        db.AssetVersions.Add(TestData.CreateAssetVersion(asset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY));
        await db.SaveChangesAsync();

        var assetStore = new AssetStore(db);
        (await assetStore.GetOwnedSellerDetail(asset.Id, stranger.Id)).Should().BeNull();
        (await assetStore.GetOwnedSellerDetail(Guid.NewGuid(), author.Id)).Should().BeNull();
    }

    [Theory]
    [InlineData(AssetVersionProcessingStatus.READY, true)]
    [InlineData(AssetVersionProcessingStatus.REJECTED, false)]
    [InlineData(AssetVersionProcessingStatus.PROCESSING_FAILED, false)]
    public async Task GetOwnedSellerDetail_WhenOwnedTerminalStates_ShouldReturnProcessingState(
        AssetVersionProcessingStatus status,
        bool expectCurrentReady)
    {
        ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Owned terminal");
        db.Assets.Add(asset);
        await db.SaveChangesAsync();
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id,
            isCurrent: status == AssetVersionProcessingStatus.READY,
            processingStatus: status);
        if (status == AssetVersionProcessingStatus.REJECTED)
        {
            version.ProcessingErrorCode = "MALWARE_DETECTED";
            version.ProcessingErrorSummary = "Malicious content detected in upload.";
        }
        else if (status == AssetVersionProcessingStatus.PROCESSING_FAILED)
        {
            version.ProcessingErrorCode = "SCANNER_UNAVAILABLE";
            version.ProcessingErrorSummary = "The malware scanner is temporarily unavailable.";
        }

        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        SellerAssetDetailItem? detail = await new AssetStore(db).GetOwnedSellerDetail(asset.Id, author.Id);

        detail.Should().NotBeNull();
        detail.LatestProcessingStatus.Should().Be(status);
        detail.LatestVersionId.Should().Be(version.Id);
        if (expectCurrentReady)
        {
            detail.CurrentReadyVersionId.Should().Be(version.Id);
        }
        else
        {
            detail.CurrentReadyVersionId.Should().BeNull();
        }
    }
}
