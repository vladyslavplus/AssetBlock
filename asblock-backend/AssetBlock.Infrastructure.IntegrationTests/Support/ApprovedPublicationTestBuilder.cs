using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Publication;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.IntegrationTests.Support;

/// <summary>Test-only trusted publication fixture; never used in demo seeding.</summary>
internal static class ApprovedPublicationTestBuilder
{
    public static async Task<(Asset Asset, AssetVersion Version, PublicationSnapshot Snapshot)> SeedApprovedListingAsync(
        ApplicationDbContext db,
        User author,
        Category category,
        string title,
        AssetVersionProcessingStatus versionStatus = AssetVersionProcessingStatus.READY)
    {
        Asset asset = TestData.CreateAsset(author.Id, category.Id, title: title);
        AssetVersion version = TestData.CreateAssetVersion(
            asset.Id,
            versionNumber: 1,
            isCurrent: false,
            processingStatus: versionStatus);

        db.Assets.Add(asset);
        db.AssetVersions.Add(version);
        await db.SaveChangesAsync();

        PublicationSnapshot snapshot = await AttachTrustedApprovedPublicationAsync(
            db,
            asset,
            version,
            author,
            category,
            title);

        return (asset, version, snapshot);
    }

    public static async Task<PublicationSnapshot> AttachTrustedApprovedPublicationAsync(
        ApplicationDbContext db,
        Asset asset,
        AssetVersion version,
        User author,
        Category category,
        string? approvedTitle = null,
        IReadOnlyList<string>? approvedTags = null)
    {
        var title = approvedTitle ?? asset.Title;
        List<string> tags = approvedTags?.ToList()
            ?? await db.AssetTags
                .AsNoTracking()
                .Where(at => at.AssetId == asset.Id)
                .OrderBy(at => at.Tag.Name)
                .Select(at => at.Tag.Name)
                .ToListAsync();
        AssetDraftWorkspace workspace = new()
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
        ModerationSubmission submission = new()
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
            State = ModerationSubmissionState.SUBMITTED,
            CaseRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow
        };
        CodeAnalysisReportHeader report = new()
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
        PublicationSnapshot snapshot = new()
        {
            Id = Guid.NewGuid(),
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            CodeAnalysisReportHeaderId = report.Id,
            ModerationSubmissionId = submission.Id,
            PolicyVersion = "policy-v1",
            ApprovedMetadataJson = ApprovedPublicationMetadata.BuildJson(title, asset.Description, category.Id, tags),
            RightsReferenceJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        };
        ModerationDecisionRecord decision = new()
        {
            Id = Guid.NewGuid(),
            SubmissionId = submission.Id,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ModeratorUserId = author.Id,
            Outcome = ModerationSubmissionState.APPROVED,
            PublicationSnapshotId = snapshot.Id,
            CaseRevision = 1,
            Message = "fixture",
            CreatedAt = DateTimeOffset.UtcNow
        };

        db.AssetDraftWorkspaces.Add(workspace);
        db.ModerationSubmissions.Add(submission);
        db.CodeAnalysisReportHeaders.Add(report);
        db.PublicationSnapshots.Add(snapshot);
        db.ModerationDecisionRecords.Add(decision);
        asset.CurrentPublicationSnapshotId = snapshot.Id;
        await db.SaveChangesAsync();
        return snapshot;
    }
}
