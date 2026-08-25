using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class AssetCatalogVisibilityPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task PublicGetPaged_WithAuthorIdFilter_MustNotExposePendingOrRejectedAssets()
    {
        var db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);

        // 1. Asset A: Has a READY current version
        var readyAsset = TestData.CreateAsset(author.Id, category.Id, title: "Ready Public Asset");
        db.Assets.Add(readyAsset);
        await db.SaveChangesAsync();
        var readyVersion = TestData.CreateAssetVersion(readyAsset.Id, isCurrent: true, processingStatus: AssetVersionProcessingStatus.READY);
        db.AssetVersions.Add(readyVersion);

        // 2. Asset B: In PENDING_INSPECTION state (not yet published)
        var pendingAsset = TestData.CreateAsset(author.Id, category.Id, title: "Pending Quarantine Asset");
        db.Assets.Add(pendingAsset);
        await db.SaveChangesAsync();
        var pendingVersion = TestData.CreateAssetVersion(pendingAsset.Id, isCurrent: false, processingStatus: AssetVersionProcessingStatus.PENDING_INSPECTION);
        db.AssetVersions.Add(pendingVersion);

        // 3. Asset C: In REJECTED state
        var rejectedAsset = TestData.CreateAsset(author.Id, category.Id, title: "Rejected Malware Asset");
        db.Assets.Add(rejectedAsset);
        await db.SaveChangesAsync();
        var rejectedVersion = TestData.CreateAssetVersion(rejectedAsset.Id, isCurrent: false, processingStatus: AssetVersionProcessingStatus.REJECTED);
        rejectedVersion.ProcessingErrorCode = "MALWARE_DETECTED";
        rejectedVersion.ProcessingErrorSummary = "Malicious content detected in upload.";
        db.AssetVersions.Add(rejectedVersion);

        await db.SaveChangesAsync();

        var assetStore = new AssetStore(db);

        // Act 1: Public catalog query WITH authorId filter (unauthenticated public browser browsing seller profile)
        var publicResult = await assetStore.GetPaged(new GetAssetsRequest
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
        var ownerResult = await assetStore.GetMyListings(author.Id, new GetAssetsRequest
        {
            Page = 1,
            PageSize = 50
        });

        // Assert 2: Owner sees all 3 assets (Ready, Pending, and Rejected)
        ownerResult.Items.Should().HaveCount(3);
        ownerResult.Items.Select(i => i.Id).Should().BeEquivalentTo([readyAsset.Id, pendingAsset.Id, rejectedAsset.Id]);
    }
}
