namespace AssetBlock.Infrastructure.Observability;

public enum DiagnosticsOutcome
{
    SUCCESS,
    FAILURE,
    HANDLER_FAILURE,
    PARTIAL_FAILURE,
    CANCELLED,
    MISSING_HANDLER,
    LEASE_LOST,
    SKIPPED_LOCKED
}

public static class DiagnosticsOutcomeExtensions
{
    public static string ToTagValue(this DiagnosticsOutcome outcome) => outcome switch
    {
        DiagnosticsOutcome.SUCCESS => "success",
        DiagnosticsOutcome.FAILURE => "failure",
        DiagnosticsOutcome.HANDLER_FAILURE => "handler_failure",
        DiagnosticsOutcome.PARTIAL_FAILURE => "partial_failure",
        DiagnosticsOutcome.CANCELLED => "cancelled",
        DiagnosticsOutcome.MISSING_HANDLER => "missing_handler",
        DiagnosticsOutcome.LEASE_LOST => "lease_lost",
        DiagnosticsOutcome.SKIPPED_LOCKED => "skipped_locked",
        _ => "unknown"
    };
}

internal enum ScanDiagnosticsOutcome
{
    CLEAN,
    INFECTED,
    LIMIT_EXCEEDED,
    UNAVAILABLE,
    ERROR,
    CANCELLED
}

internal enum ScanByteKind
{
    READ,
    ATTEMPTED,
    TRANSFERRED
}

internal enum AiDiagnosticsOutcome
{
    SUCCESS,
    DISABLED,
    RETRYABLE,
    TERMINAL,
    CANCELLED
}
