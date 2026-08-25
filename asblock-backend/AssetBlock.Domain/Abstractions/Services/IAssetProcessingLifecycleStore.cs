using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;

public sealed record BoundedArchiveAnalysisRecord(
    int FileCount,
    long TotalExpandedBytes,
    string? ReadmeContent,
    ArchiveAnalysisManifestMetadata? ManifestMetadata
);

/// <summary>
/// Authoritative, fenced database transitions for asset version processing lifecycle.
/// Every transition validates job fencing (running, matching lease token, unexpired lease after lock wait)
/// and executes version mutation, analysis persistence, and monotonic promotion atomically in one transaction.
/// </summary>
public interface IAssetProcessingLifecycleStore
{
    Task<bool> TransitionArchiveInspectionAccepted(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        ArchiveInspectionResult result,
        BoundedArchiveAnalysisRecord analysis,
        CancellationToken cancellationToken = default);

    Task<bool> TransitionArchiveInspectionRejected(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default);

    Task<bool> TransitionArchiveInspectionFailed(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default);

    Task<bool> TransitionMalwareScanClean(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        MalwareScanResult result,
        CancellationToken cancellationToken = default);

    Task<bool> TransitionMalwareScanRejected(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default);

    Task<bool> TransitionMalwareScanFailed(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default);

    Task<bool> TransitionProcessingFailed(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        AssetProcessingJobType jobType,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default);
}
