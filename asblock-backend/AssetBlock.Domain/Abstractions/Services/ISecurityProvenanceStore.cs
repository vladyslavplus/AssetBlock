using AssetBlock.Domain.Core.Dto;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Security recheck lifecycle for versions that are already READY but lack sufficient security
/// provenance. Every method follows the Asset -> recheck -> job lock order and is fenced by the
/// job lease token and the exact security generation. A READY version is never pushed back through
/// PENDING_INSPECTION or PENDING_MALWARE_SCAN, and its original jobs and results are preserved.
/// </summary>
public interface ISecurityProvenanceStore
{
    Task<SecurityRecheckStartResult> StartSecurityRecheck(
        SecurityRecheckStartRequest request,
        CancellationToken cancellationToken = default);

    Task<SecurityRecheckTransitionResult> TransitionRecheckArchiveAccepted(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        ArchiveValidationEvidence evidence,
        CancellationToken cancellationToken = default);

    Task<SecurityRecheckTransitionResult> TransitionRecheckArchiveUnsafe(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default);

    Task<SecurityRecheckTransitionResult> TransitionRecheckMalwareClean(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        MalwareScanEvidence evidence,
        CancellationToken cancellationToken = default);

    Task<SecurityRecheckTransitionResult> TransitionRecheckMalwareInfected(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        MalwareScanEvidence? evidence,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Quarantines a READY version whose stored bytes failed authentication (decryptor AEAD or
    /// truncation fault). State UNSAFE with an integrity-mismatch restriction truthfully
    /// identifies the failure: never INFECTED, never a silent operational error. Entitlement and
    /// history are preserved; only byte access is denied. The READY status is never reset.
    /// </summary>
    Task<SecurityRecheckTransitionResult> TransitionRecheckIntegrityMismatch(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default);

    Task<SecurityRecheckTransitionResult> TransitionRecheckFailed(
        Guid jobId,
        Guid leaseToken,
        Guid assetId,
        Guid assetVersionId,
        string errorCode,
        string safeSummary,
        CancellationToken cancellationToken = default);
}
