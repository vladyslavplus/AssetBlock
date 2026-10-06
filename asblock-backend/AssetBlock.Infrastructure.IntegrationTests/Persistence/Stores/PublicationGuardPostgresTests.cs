using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Publication;
using AssetBlock.Infrastructure.IntegrationTests.Support;
using AssetBlock.Infrastructure.Persistence;
using AssetBlock.Infrastructure.Persistence.Stores;

namespace AssetBlock.Infrastructure.IntegrationTests.Persistence.Stores;

[Collection(nameof(PostgresStoreCollection))]
public sealed class PublicationGuardPostgresTests(PostgresFixture fixture)
{
    [Fact]
    public async Task GetPaged_WhenAssetIsReadyWithoutApproval_ShouldExcludeFromCatalog()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Ready but private");
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id,
            isCurrent: true,
            processingStatus: AssetVersionProcessingStatus.READY);
        var store = new AssetStore(db);
        await store.AddWithVersion(asset, version, tags: null);

        CatalogPageResult<AssetListItem> paged = await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10 });
        paged.Items.Should().BeEmpty();
        paged.TotalCount.Should().Be(0);

        (await store.GetCurrentVersionSnapshot(asset.Id)).Should().BeNull();
    }

    [Fact]
    public async Task GetPaged_WhenAssetHasTrustedApprovedSnapshot_ShouldIncludeInCatalog()
    {
        await using ApplicationDbContext db = await fixture.CreateCleanDbContext();
        (User author, Category category) = await TestData.SeedAuthorAndCategory(db);
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: "Approved listing");
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id,
            isCurrent: true,
            processingStatus: AssetVersionProcessingStatus.READY);
        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        PublicationSnapshot snapshot = await SeedTrustedApprovedSnapshotAsync(db, asset, version, author);

        asset.CurrentPublicationSnapshotId = snapshot.Id;
        await db.SaveChangesAsync();

        var store = new AssetStore(db);
        CatalogPageResult<AssetListItem> paged = await store.GetPaged(new GetAssetsRequest { Page = 1, PageSize = 10 });

        paged.TotalCount.Should().Be(1);
        paged.Items.Should().ContainSingle(i => i.Id == asset.Id);

        AssetCurrentVersionSnapshot? offering = await store.GetCurrentVersionSnapshot(asset.Id);
        offering.Should().NotBeNull();
        offering!.AssetVersionId.Should().Be(version.Id);
        offering.PublicationSnapshotId.Should().Be(snapshot.Id);
    }

    private static async Task<PublicationSnapshot> SeedTrustedApprovedSnapshotAsync(
        ApplicationDbContext db,
        Asset asset,
        AssetVersion version,
        User author)
    {
        var workspace = new AssetDraftWorkspace
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceVersionScopeKey = version.Id,
            ScopeId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            WorkspaceRevision = 1,
            CaseRevision = 1
        };
        db.AssetDraftWorkspaces.Add(workspace);

        var submission = new ModerationSubmission
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspace.Id,
            WorkspaceVersionScopeKey = workspace.WorkspaceVersionScopeKey,
            OwnerUserId = author.Id,
            ContentSha256 = version.ContentSha256,
            DeclarationRevision = 1,
            MaterialMetadataRevision = 1,
            SellerEvidenceRevision = 1,
            PolicyVersion = "policy-v1",
            SellerEvidenceDigest = new string('d', 64),
            State = ModerationSubmissionState.APPROVED,
            CaseRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.ModerationSubmissions.Add(submission);

        var report = new CodeAnalysisReportHeader
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            PolicyVersion = "policy-v1",
            InputRevision = 1,
            Purpose = CodeAnalysisReportPurpose.PRODUCTION,
            CanAuthorizePublication = true,
            IsFinalized = true,
            FinalizedAt = DateTimeOffset.UtcNow,
            ReportSchemaVersion = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.CodeAnalysisReportHeaders.Add(report);

        var snapshot = new PublicationSnapshot
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            CodeAnalysisReportHeaderId = report.Id,
            ModerationSubmissionId = submission.Id,
            PolicyVersion = "policy-v1",
            ApprovedMetadataJson = ApprovedPublicationMetadata.BuildJson(asset.Title, asset.Description, asset.CategoryId, []),
            RightsReferenceJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        db.PublicationSnapshots.Add(snapshot);

        db.ModerationDecisionRecords.Add(new ModerationDecisionRecord
        {
            Id = Guid.NewGuid(),
            SubmissionId = submission.Id,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ModeratorUserId = author.Id,
            Outcome = ModerationSubmissionState.APPROVED,
            PublicationSnapshotId = snapshot.Id,
            CaseRevision = 1,
            Message = "fixture approval",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
        return snapshot;
    }
}
