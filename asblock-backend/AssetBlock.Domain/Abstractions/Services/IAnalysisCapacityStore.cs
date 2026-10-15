using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Shared, durable capacity for an external analysis service. Waiting for capacity never consumes a
/// job attempt, and a reservation is only released once the old execution is confirmed gone.
/// </summary>
public interface IAnalysisCapacityStore
{
    /// <summary>
    /// Registers the service identity and creates its slot set. Serialized on the registration row,
    /// so concurrent startups with different capacities cannot converge on a mixed configuration.
    /// </summary>
    Task<int> EnsureSlots(
        string serviceKey,
        int capacity,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically reserves one free slot and claims one eligible queued analysis job bound to this
    /// service. When no slot is free nothing is claimed, no job transitions to RUNNING and no
    /// attempt is consumed.
    /// </summary>
    Task<IReadOnlyList<ClaimedAssetProcessingJob>> ClaimWithCapacity(
        int batchSize,
        TimeSpan leaseDuration,
        TimeSpan hardDeadline,
        TimeSpan cleanupGrace,
        string leaseOwner,
        CancellationToken cancellationToken = default);

    /// <summary>Extends only the job lease; the immutable hard deadline is never extended.</summary>
    Task<bool> RenewAnalysisLease(
        Guid jobId,
        Guid leaseToken,
        TimeSpan leaseDuration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the slot bound to a job using the execution disposition actually observed. An
    /// unconfirmed outcome leaves the reservation fenced until the enforced deadline plus grace.
    /// </summary>
    Task<bool> ReleaseReservation(
        Guid slotId,
        string transportLeaseToken,
        AnalysisExecutionDisposition disposition,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Refunds exactly one attempt for an authenticated refusal the service returned before accepting
    /// any work. Only the exact live claim under its own reservation identity may be refunded.
    /// </summary>
    Task<bool> RefundNotStartedClaim(
        Guid jobId,
        Guid leaseToken,
        string transportLeaseToken,
        int reservationGeneration,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Releases the slot bound to a job that has just reached a terminal state, so a finished
    /// execution does not have to wait for deadline reconciliation before capacity returns.
    /// </summary>
    Task<bool> ReleaseForTerminalJob(
        Guid jobId,
        string transportLeaseToken,
        AnalysisExecutionDisposition disposition,
        CancellationToken cancellationToken = default);

    Task<int> RecoverExpiredReservations(CancellationToken cancellationToken = default);
}
