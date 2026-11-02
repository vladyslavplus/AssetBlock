using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Publication;
using AssetBlock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Application.Tests.SearchEvaluation;

/// <summary>Trusted approved publication rows for isolated SearchEvaluation PostgreSQL fixtures.</summary>
internal static class TrustedPublicationFixtureSeed
{
    public static async Task Attach(
        ApplicationDbContext db,
        Asset asset,
        AssetVersion version,
        Guid authorId,
        Category category,
        string approvedTitle,
        string? approvedDescription = null,
        IReadOnlyList<string>? approvedTags = null)
    {
        List<string> tags = approvedTags?.ToList()
            ?? await db.AssetTags
                .AsNoTracking()
                .Where(at => at.AssetId == asset.Id)
                .OrderBy(at => at.Tag.Name)
                .Select(at => at.Tag.Name)
                .ToListAsync();

        var workspaceId = Guid.NewGuid();
        var submissionId = Guid.NewGuid();
        var snapshotId = Guid.NewGuid();

        db.AssetDraftWorkspaces.Add(new AssetDraftWorkspace
        {
            Id = workspaceId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceVersionScopeKey = version.Id,
            ScopeId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            WorkspaceRevision = 1,
            CaseRevision = 1
        });
        db.ModerationSubmissions.Add(new ModerationSubmission
        {
            Id = submissionId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            WorkspaceId = workspaceId,
            WorkspaceVersionScopeKey = version.Id,
            OwnerUserId = authorId,
            ContentSha256 = version.ContentSha256,
            DeclarationRevision = 1,
            MaterialMetadataRevision = 1,
            SellerEvidenceRevision = 1,
            PolicyVersion = "policy-v1",
            SellerEvidenceDigest = new string('d', 64),
            State = ModerationSubmissionState.SUBMITTED,
            CaseRevision = 1,
            CreatedAt = DateTimeOffset.UtcNow
        });
        // Trusted publication needs the whole evidence chain, not a bare boolean header.
        AdmittedReportTestEvidence.Chain chain = AdmittedReportTestEvidence.BuildChain();
        db.CodeAnalysisPolicyVersions.Add(AdmittedReportTestEvidence.Policy(chain));
        db.SecurityEvidenceRecords.Add(AdmittedReportTestEvidence.ArchiveEvidence(version, chain));
        db.SecurityEvidenceRecords.Add(AdmittedReportTestEvidence.MalwareEvidence(version, chain));
        db.CodeAnalysisRuns.Add(AdmittedReportTestEvidence.CompletedRun(version, chain));
        CodeAnalysisReportHeader seededReport = AdmittedReportTestEvidence.Header(version, chain);
        db.CodeAnalysisReportHeaders.Add(seededReport);
        db.CodeAnalysisReportAdmissions.Add(AdmittedReportTestEvidence.Admission(seededReport, chain, reviewEligible: true));
        db.PublicationSnapshots.Add(new PublicationSnapshot
        {
            Id = snapshotId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ContentSha256 = version.ContentSha256,
            CodeAnalysisReportHeaderId = seededReport.Id,
            ModerationSubmissionId = submissionId,
            PolicyVersion = "policy-v1",
            ApprovedMetadataJson = ApprovedPublicationMetadata.BuildJson(
                approvedTitle,
                approvedDescription ?? asset.Description,
                category.Id,
                tags),
            RightsReferenceJson = "{}",
            CreatedAt = DateTimeOffset.UtcNow
        });
        db.ModerationDecisionRecords.Add(new ModerationDecisionRecord
        {
            Id = Guid.NewGuid(),
            SubmissionId = submissionId,
            AssetId = asset.Id,
            AssetVersionId = version.Id,
            ModeratorUserId = authorId,
            Outcome = ModerationSubmissionState.APPROVED,
            PublicationSnapshotId = snapshotId,
            CaseRevision = 1,
            Message = "fixture",
            CreatedAt = DateTimeOffset.UtcNow
        });

        await db.SaveChangesAsync();
        asset.CurrentPublicationSnapshotId = snapshotId;
        await db.SaveChangesAsync();
    }
}
