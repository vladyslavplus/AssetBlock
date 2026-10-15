using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Core.Dto;

/// <summary>
/// How an analysis execution actually ended, as observed by the caller. Capacity is only reclaimed
/// on evidence that the external execution is gone; a database status or a finished HTTP task is not
/// evidence on its own.
/// </summary>
public enum AnalysisExecutionDisposition
{
    /// <summary>The service refused before accepting any work, so nothing was ever started.</summary>
    NOT_STARTED,

    /// <summary>A complete response was received and the execution is known to have finished.</summary>
    CONFIRMED_TERMINATED,

    /// <summary>
    /// Timeout, disconnect or an ambiguous outcome after acceptance. The execution may still be
    /// running, so the reservation stays fenced until the enforced deadline plus cleanup grace.
    /// </summary>
    UNCONFIRMED
}

/// <summary>Why a reservation was released, recorded truthfully on the slot.</summary>
public static class AnalysisReleaseReasons
{
    public const string COMPLETED = "COMPLETED";
    public const string TERMINAL_FAILURE = "TERMINAL_FAILURE";
    public const string CONFIRMED_TERMINATION = "CONFIRMED_TERMINATION";
    public const string NOT_STARTED_REFUND = "NOT_STARTED_REFUND";
    public const string DEADLINE_RECONCILED = "DEADLINE_RECONCILED";
    public const string LEASE_RECOVERED = "LEASE_RECOVERED";
}
