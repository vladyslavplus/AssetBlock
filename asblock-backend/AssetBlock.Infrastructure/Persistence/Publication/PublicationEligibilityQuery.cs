using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Publication;
using Microsoft.EntityFrameworkCore;

namespace AssetBlock.Infrastructure.Persistence.Publication;

/// <summary>SQL-translatable publication predicates shared by catalog, commerce, and download stores.</summary>
internal static class PublicationEligibilityQuery
{
    public static IQueryable<PublicationSnapshot> TrustedApprovedSnapshots(ApplicationDbContext db) =>
        db.PublicationSnapshots.AsNoTracking()
            .Where(s =>
                db.ModerationDecisionRecords.Any(d =>
                    d.PublicationSnapshotId == s.Id
                    && d.AssetId == s.AssetId
                    && d.AssetVersionId == s.AssetVersionId
                    && d.Outcome == ModerationSubmissionState.APPROVED)
                && db.CodeAnalysisReportHeaders.Any(h =>
                    h.Id == s.CodeAnalysisReportHeaderId
                    && h.AssetId == s.AssetId
                    && h.AssetVersionId == s.AssetVersionId
                    && h.ContentSha256 == s.ContentSha256
                    && h.Purpose == CodeAnalysisReportPurpose.PRODUCTION
                    && h.CanAuthorizePublication
                    && h.IsFinalized));

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
                && TrustedApprovedSnapshots(db).Any(s =>
                    s.AssetId == assetId
                    && s.AssetVersionId == v.Id
                    && s.ContentSha256 == v.ContentSha256));

    public static IQueryable<AssetVersion> ApprovedVersionHistoryForAsset(ApplicationDbContext db, Guid assetId) =>
        db.AssetVersions.AsNoTracking()
            .Where(v =>
                v.AssetId == assetId
                && v.ProcessingStatus == AssetVersionProcessingStatus.READY
                && TrustedApprovedSnapshots(db).Any(s =>
                    s.AssetId == assetId
                    && s.AssetVersionId == v.Id
                    && s.ContentSha256 == v.ContentSha256));

    public static bool IsAuthorSafePreviewVersion(AssetVersion version) =>
        PublicationEligibility.IsAuthorSafePreviewVersion(version);
}
