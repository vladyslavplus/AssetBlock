using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Publication;

/// <summary>
/// Shared publication, sale, and download-safety rules.
/// EF queries compose these predicates; they are not satisfied by READY or IsCurrent alone.
/// </summary>
public static class PublicationEligibility
{
    public static bool IsTrustedProductionReport(CodeAnalysisReportHeader header) =>
        header is { Purpose: CodeAnalysisReportPurpose.PRODUCTION, CanAuthorizePublication: true, IsFinalized: true };

    public static bool SnapshotMatchesVersion(PublicationSnapshot snapshot, AssetVersion version) =>
        snapshot.AssetId == version.AssetId
        && snapshot.AssetVersionId == version.Id
        && string.Equals(snapshot.ContentSha256, version.ContentSha256, StringComparison.Ordinal);

    private static bool IsSafeProcessedVersion(AssetVersion version) =>
        version.ProcessingStatus == AssetVersionProcessingStatus.READY;

    public static bool IsUnsafeProcessedVersion(AssetVersion version) =>
        version.ProcessingStatus is AssetVersionProcessingStatus.REJECTED
            or AssetVersionProcessingStatus.PROCESSING_FAILED;

    /// <summary>Owner preview requires completed mandatory security checks (READY).</summary>
    public static bool IsAuthorSafePreviewVersion(AssetVersion version) =>
        IsSafeProcessedVersion(version);
}
