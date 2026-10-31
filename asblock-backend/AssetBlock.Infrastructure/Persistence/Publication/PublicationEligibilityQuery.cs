using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Publication;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Publication;

/// <summary>SQL-translatable publication predicates shared by catalog, commerce, and download stores.</summary>
internal static class PublicationEligibilityQuery
{
    /// <summary>
    /// A report is trusted for publication only when a completed analysis run produced it and a
    /// server admission record binds that run's exact input. A finalized header on its own, a header
    /// with no bound run, or any admission field supplied by the analysis service proves nothing, so
    /// pre-existing boolean headers stay untrusted.
    /// </summary>
    public static IQueryable<CodeAnalysisReportHeader> AdmittedReports(ApplicationDbContext db) =>
        db.CodeAnalysisReportHeaders.AsNoTracking()
            .Where(h =>
                h.IsFinalized
                && h.AnalysisRunId != null
                && h.PythonReportId != null
                && h.ReportDigest != null
                && db.CodeAnalysisReportAdmissions.Any(a =>
                    a.ReportId == h.Id
                    && a.PythonReportId == h.PythonReportId
                    && a.ReportDigest == h.ReportDigest
                    && a.InputDigest == db.CodeAnalysisRuns
                        .Where(r =>
                            r.Id == h.AnalysisRunId
                            && r.State == CodeAnalysisRunState.COMPLETED
                            && r.AssetId == h.AssetId
                            && r.AssetVersionId == h.AssetVersionId
                            && r.ContentSha256 == h.ContentSha256)
                        .Select(r => r.InputDigest)
                        .FirstOrDefault()
                    && db.CodeAnalysisRuns.Any(r =>
                        r.Id == h.AnalysisRunId
                        && r.State == CodeAnalysisRunState.COMPLETED
                        && r.AssetId == h.AssetId
                        && r.AssetVersionId == h.AssetVersionId
                        && r.ContentSha256 == h.ContentSha256
                        && db.CodeAnalysisPolicyVersions.Any(p =>
                            p.Id == r.PolicyVersionId
                            && p.PolicyKey == h.PolicyVersion
                            && p.PolicyDigest == r.PolicyDigest))));

    public static IQueryable<PublicationSnapshot> TrustedApprovedSnapshots(ApplicationDbContext db) =>
        db.PublicationSnapshots.AsNoTracking()
            .Where(s =>
                db.ModerationDecisionRecords.Any(d =>
                    d.PublicationSnapshotId == s.Id
                    && d.AssetId == s.AssetId
                    && d.AssetVersionId == s.AssetVersionId
                    && d.Outcome == ModerationSubmissionState.APPROVED)
                // Only the approved decision grants publication. Report admission still proves
                // provenance; review eligibility is not publication authority.
                && AdmittedReports(db).Any(h =>
                    h.Id == s.CodeAnalysisReportHeaderId
                    && h.AssetId == s.AssetId
                    && h.AssetVersionId == s.AssetVersionId
                    && h.ContentSha256 == s.ContentSha256
                    && h.Purpose == CodeAnalysisReportPurpose.PRODUCTION));

    public static IQueryable<Asset> PublicCatalogAssets(ApplicationDbContext db) =>
        db.Assets.AsNoTracking()
            .Where(a =>
                a.DeletedAt == null
                && a.CurrentPublicationSnapshotId != null
                && TrustedApprovedSnapshots(db).Any(s =>
                    s.Id == a.CurrentPublicationSnapshotId
                    && s.AssetId == a.Id
                    && db.AssetVersions.Any(v =>
                        v.Id == s.AssetVersionId
                        && v.AssetId == a.Id
                        && v.ProcessingStatus == AssetVersionProcessingStatus.READY
                        && v.SafetyRestriction == AssetVersionSafetyRestriction.NONE
                        && v.ContentSha256 == s.ContentSha256)));

    public static IQueryable<PublicationSnapshot> SaleEligibleSnapshot(
        ApplicationDbContext db,
        Guid assetId,
        Guid assetVersionId) =>
        TrustedApprovedSnapshots(db)
            .Where(s =>
                s.AssetId == assetId
                && s.AssetVersionId == assetVersionId
                && db.Assets.Any(a => a.Id == assetId && a.DeletedAt == null)
                && db.AssetVersions.Any(v =>
                    v.Id == assetVersionId
                    && v.AssetId == assetId
                    && v.ProcessingStatus == AssetVersionProcessingStatus.READY
                    && v.SafetyRestriction == AssetVersionSafetyRestriction.NONE
                    && v.ContentSha256 == s.ContentSha256));

    public static IQueryable<PublicationSnapshot> SaleEligibleSnapshotById(
        ApplicationDbContext db,
        Guid assetId,
        Guid publicationSnapshotId,
        Guid assetVersionId) =>
        SaleEligibleSnapshot(db, assetId, assetVersionId)
            .Where(s => s.Id == publicationSnapshotId);

    public static IQueryable<AssetVersion> BuyerAccessibleVersions(
        ApplicationDbContext db,
        Guid assetId,
        int purchasedVersionNumber) =>
        db.AssetVersions.AsNoTracking()
            .Where(v =>
                v.AssetId == assetId
                && v.VersionNumber >= purchasedVersionNumber
                && v.ProcessingStatus == AssetVersionProcessingStatus.READY
                && v.SafetyRestriction == AssetVersionSafetyRestriction.NONE
                && TrustedApprovedSnapshots(db).Any(s =>
                    s.AssetId == assetId
                    && s.AssetVersionId == v.Id
                    && s.ContentSha256 == v.ContentSha256));

    public static IQueryable<AssetVersion> ApprovedVersionHistoryForAsset(ApplicationDbContext db, Guid assetId) =>
        db.AssetVersions.AsNoTracking()
            .Where(v =>
                v.AssetId == assetId
                && v.ProcessingStatus == AssetVersionProcessingStatus.READY
                && v.SafetyRestriction == AssetVersionSafetyRestriction.NONE
                && TrustedApprovedSnapshots(db).Any(s =>
                    s.AssetId == assetId
                    && s.AssetVersionId == v.Id
                    && s.ContentSha256 == v.ContentSha256));

    /// <summary>Exact-version bytes an owner may still read before an approved fallback is considered.</summary>
    public static IQueryable<AssetVersion> OwnerSafePreviewVersions(ApplicationDbContext db, Guid assetId, Guid assetVersionId) =>
        db.AssetVersions.AsNoTracking()
            .Where(v =>
                v.Id == assetVersionId
                && v.AssetId == assetId
                && v.ProcessingStatus == AssetVersionProcessingStatus.READY
                && v.SafetyRestriction == AssetVersionSafetyRestriction.NONE);

    public static bool IsAuthorSafePreviewVersion(AssetVersion version) =>
        PublicationEligibility.IsAuthorSafePreviewVersion(version);
}
