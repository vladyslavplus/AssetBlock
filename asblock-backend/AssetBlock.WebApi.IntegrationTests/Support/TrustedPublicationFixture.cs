using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Publication;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.WebApi.IntegrationTests.Support;

internal static class TrustedPublicationFixture
{
    public static async Task AttachTrustedApprovedPublication(
        ApplicationDbContext db,
        Asset asset,
        AssetVersion version,
        User author,
        Category category,
        string? approvedTitle = null,
        IReadOnlyList<string>? approvedTags = null)
    {
        var title = approvedTitle ?? asset.Title;
        var alreadyApprovedForVersion = await db.ModerationDecisionRecords.AsNoTracking()
            .AnyAsync(d =>
                d.AssetId == asset.Id
                && d.AssetVersionId == version.Id
                && d.Outcome == ModerationSubmissionState.APPROVED);
        if (alreadyApprovedForVersion)
        {
            return;
        }

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
    }

    public static async Task AttachTrustedPublicationForAllReadyAssets(ApplicationDbContext db)
    {
        List<Asset> assets = await db.Assets
            .Where(a => a.DeletedAt == null && a.CurrentPublicationSnapshotId == null)
            .ToListAsync();

        foreach (Asset asset in assets)
        {
            AssetVersion? version = await db.AssetVersions
                .Where(v => v.AssetId == asset.Id && v.ProcessingStatus == AssetVersionProcessingStatus.READY)
                .OrderByDescending(v => v.IsCurrent)
                .ThenByDescending(v => v.VersionNumber)
                .FirstOrDefaultAsync();
            if (version is null)
            {
                continue;
            }

            User author = await db.Users.FirstAsync(u => u.Id == asset.AuthorId);
            Category category = await db.Categories.FirstAsync(c => c.Id == asset.CategoryId);
            await AttachTrustedApprovedPublication(db, asset, version, author, category, asset.Title);
        }
    }
}
