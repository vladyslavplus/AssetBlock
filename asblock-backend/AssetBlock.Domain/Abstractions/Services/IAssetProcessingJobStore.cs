using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAssetProcessingJobStore
{
    Task<Guid> Enqueue(
        Guid assetId,
        Guid assetVersionId,
        AssetProcessingJobType type,
        int definitionVersion,
        TimeSpan initialDelay,
        AssetProcessingPayload payload,
        string? traceParent = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ClaimedAssetProcessingJob>> ClaimPendingBatch(
        int batchSize,
        TimeSpan leaseDuration,
        string leaseOwner,
        CancellationToken cancellationToken = default);

    Task<bool> RenewLease(
        Guid jobId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    Task<bool> MarkSucceeded(
        Guid jobId,
        Guid leaseToken,
        AssetProcessingResult? result,
        CancellationToken cancellationToken = default);

    Task<bool> MarkFailedRetryable(
        Guid jobId,
        Guid leaseToken,
        string errorCode,
        string errorSummary,
        TimeSpan retryDelay,
        CancellationToken cancellationToken = default);

    Task<bool> MarkFailedTerminal(
        Guid jobId,
        Guid leaseToken,
        string errorCode,
        string errorSummary,
        CancellationToken cancellationToken = default);

    Task<bool> MarkCancelled(
        Guid jobId,
        Guid leaseToken,
        CancellationToken cancellationToken = default);

    Task<int> RecoverExpiredLeases(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetProcessingJobDto>?> GetJobsForAsset(
        Guid assetId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AssetProcessingJobDto>?> GetJobsForVersion(
        Guid assetVersionId,
        Guid ownerUserId,
        CancellationToken cancellationToken = default);

    Task<AssetProcessingJobRealtimeState?> GetRealtimeState(
        Guid jobId,
        CancellationToken cancellationToken = default);
}
