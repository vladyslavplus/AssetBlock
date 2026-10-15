namespace AssetBlock.Domain.Core.Enums;

/// <summary>
/// Why an analysis enqueue produced no bound run/job. Recovery maps each reason to either a
/// durable retry or an explicit drop, so a stale intent is never silently admitted and a
/// recoverable gap is never silently lost.
/// </summary>
public enum AnalysisEnqueueRejection
{
    /// <summary>A complete coherent run/job pair exists.</summary>
    NONE,

    /// <summary>The exact pinned policy row is not persisted yet; retrying can resolve it.</summary>
    POLICY_UNAVAILABLE,

    /// <summary>The advertised asset/version is no longer current (deleted, edited or quarantined).</summary>
    NOT_CURRENT,

    /// <summary>A newer security selection replaced the advertised proof; nothing is scheduled.</summary>
    PROOF_SUPERSEDED,

    /// <summary>The advertised proof itself is not admissible; the intent never schedules.</summary>
    PROOF_INVALID,

    /// <summary>An older live run still owns this selection generation; retry after it settles.</summary>
    PENDING_LIVE_RUN,
}
