namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Leased finalizer for a confirmed-terminated analysis response. Owns report, stage,
/// finding, admission, selection, job and run completion in one commit under the job lease.
/// </summary>
public interface ICodeAnalysisReportFinalizer
{
    Task<AssetProcessingJobOutcome> FinalizeConfirmed(
        Guid jobId,
        Guid leaseToken,
        string responseBody,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Fenced exact-version quarantine after a proven integrity failure. Only runs under the
    /// live job lease; a lost lease never authorizes an unfenced mutation.
    /// </summary>
    Task<AssetProcessingJobOutcome> QuarantineIntegrity(
        Guid jobId,
        Guid leaseToken,
        string errorCode,
        CancellationToken cancellationToken = default);
}
