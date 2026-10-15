using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;
/// <summary>
/// Creates the immutable analysis run and its single bound job for an already admitted security
/// evidence pair. Implementations join the caller's transaction so evidence, run, job, audit and
/// outbox commit together.
/// </summary>
public interface IAnalysisRunEnqueuer
{
    Task<AnalysisRunEnqueueResult> Enqueue(
        Guid assetId,
        Guid assetVersionId,
        Guid? securityRecheckId,
        Guid archiveEvidenceId,
        Guid malwareEvidenceId,
        int securityGeneration,
        string contentSha256,
        DateTimeOffset databaseNow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Records one durable scheduling intent in the caller's transaction when the run/job pair
    /// cannot be created yet (policy not pinned, or an older live run still owns the selection
    /// generation). Recovery rereads the persisted proof instead of trusting this envelope.
    /// </summary>
    Task PersistSchedulingIntent(
        Guid assetId,
        Guid assetVersionId,
        Guid? securityRecheckId,
        Guid archiveEvidenceId,
        Guid malwareEvidenceId,
        int securityGeneration,
        string contentSha256,
        DateTimeOffset databaseNow,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Ensures initial-upload security evidence (generation 1, unscoped) from actually observed
    /// verification and enqueues the single bound analysis run for a newly READY version.
    /// Safe CLEAN only; quarantine guards stay upstream. Joins the caller's transaction so
    /// evidence, run and job commit together; never fabricates proof from expected values.
    /// </summary>
    Task<InitialUploadProofResult> EnqueueForInitialUpload(
        Guid assetId,
        Guid assetVersionId,
        string contentSha256,
        int malwareDefinitionVersion,
        MalwareScanEvidence evidence,
        Guid jobId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Bound run and job produced by a successful analysis admission, or the exact rejection when no
/// pair was created. A non-empty run id is always accompanied by its bound job id. A rejection is
/// retryable only when it names an operational gap a later attempt can resolve; an inadmissible
/// proof or a superseded target never becomes current again, so it must not be retried.
/// </summary>
public sealed record AnalysisRunEnqueueResult(
    Guid AnalysisRunId,
    Guid AnalysisJobId,
    bool IsReplay,
    AnalysisEnqueueRejection Rejection = AnalysisEnqueueRejection.NONE)
{
    public bool IsRetryable => Rejection is AnalysisEnqueueRejection.POLICY_UNAVAILABLE
        or AnalysisEnqueueRejection.PENDING_LIVE_RUN;
}

/// <summary>
/// Outcome of persisting observed initial-upload proof. Evidence is persisted before any
/// analysis admission; analysis is enqueued only when the pinned policy and the archive
/// proof the run must bind are both present.
/// </summary>
public sealed record InitialUploadProofResult(
    bool EvidencePersisted,
    Guid ArchiveEvidenceId,
    Guid MalwareEvidenceId,
    Guid AnalysisRunId,
    Guid AnalysisJobId,
    bool AnalysisEnqueued);
