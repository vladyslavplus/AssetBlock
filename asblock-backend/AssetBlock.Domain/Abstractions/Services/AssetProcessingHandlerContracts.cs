using System.Text.RegularExpressions;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Execution context provided to typed job handlers. Contains safe identifiers and typed payload without EF entities or stores.
/// </summary>
public sealed record AssetProcessingJobContext<TPayload>(
    Guid JobId,
    Guid LeaseToken,
    Guid AssetId,
    Guid AssetVersionId,
    int DefinitionVersion,
    int AttemptCount,
    int MaxAttempts,
    TPayload Payload,
    string? TraceParent,
    CancellationToken CancellationToken,
    Guid? SecurityRecheckId = null
) where TPayload : AssetProcessingPayload;

/// <summary>
/// Closed set of explicit immutable outcomes returned by typed job handlers.
/// Uses sealed classes with internal constructors and get-only properties to prevent bypassing factory validation.
/// </summary>
public abstract partial class AssetProcessingJobOutcome
{
    private static readonly Regex _errorCodeRegex = MyRegex();

    private AssetProcessingJobOutcome() { }

    public sealed class Success : AssetProcessingJobOutcome
    {
        public AssetProcessingResult Result { get; }

        internal Success(AssetProcessingResult result)
        {
            Result = result;
        }

        public void Deconstruct(out AssetProcessingResult result) => result = Result;
    }

    public sealed class RetryableFailure : AssetProcessingJobOutcome
    {
        public string ErrorCode { get; }
        public string SafeSummary { get; }
        public TimeSpan? RetryAfter { get; }

        internal RetryableFailure(string errorCode, string safeSummary, TimeSpan? retryAfter = null)
        {
            ErrorCode = errorCode;
            SafeSummary = safeSummary;
            RetryAfter = retryAfter;
        }

        public void Deconstruct(out string errorCode, out string safeSummary, out TimeSpan? retryAfter)
        {
            errorCode = ErrorCode;
            safeSummary = SafeSummary;
            retryAfter = RetryAfter;
        }
    }

    public sealed class TerminalFailure : AssetProcessingJobOutcome
    {
        public string ErrorCode { get; }
        public string SafeSummary { get; }

        internal TerminalFailure(string errorCode, string safeSummary)
        {
            ErrorCode = errorCode;
            SafeSummary = safeSummary;
        }

        public void Deconstruct(out string errorCode, out string safeSummary)
        {
            errorCode = ErrorCode;
            safeSummary = SafeSummary;
        }
    }

    /// <summary>
    /// A handler that called an external execution system reports what it actually observed about that
    /// execution alongside its normal outcome. Shared capacity is only returned on that evidence, so
    /// handlers that never call an external service do not use this case.
    /// </summary>
    public sealed class ExternalExecution : AssetProcessingJobOutcome
    {
        public AssetProcessingJobOutcome Inner { get; }

        public AnalysisExecutionDisposition Disposition { get; }

        internal ExternalExecution(AssetProcessingJobOutcome inner, AnalysisExecutionDisposition disposition)
        {
            Inner = inner;
            Disposition = disposition;
        }
    }

    /// <summary>
    /// The lifecycle store already committed the job transition atomically (SUCCEEDED or FAILED).
    /// The worker must skip the second DB transition but still publish the final SignalR state.
    /// </summary>
    public sealed class AtomicCommitted : AssetProcessingJobOutcome
    {
        public AssetProcessingJobStatus JobStatus { get; }

        internal AtomicCommitted(AssetProcessingJobStatus jobStatus)
        {
            JobStatus = jobStatus;
        }
    }

    public static AssetProcessingJobOutcome Succeeded(AssetProcessingResult result)
    {
        ArgumentNullException.ThrowIfNull(result);
        return new Success(result);
    }

    public static AssetProcessingJobOutcome CommittedSucceeded() =>
        new AtomicCommitted(AssetProcessingJobStatus.SUCCEEDED);

    public static AssetProcessingJobOutcome CommittedFailed() =>
        new AtomicCommitted(AssetProcessingJobStatus.FAILED);

    public static AssetProcessingJobOutcome CommittedRetryScheduled() =>
        new AtomicCommitted(AssetProcessingJobStatus.RETRY_SCHEDULED);

    public static AssetProcessingJobOutcome Retryable(string errorCode, string safeSummary, TimeSpan? retryAfter = null)
    {
        ValidateError(errorCode, safeSummary);
        if (retryAfter.HasValue && retryAfter.Value < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(retryAfter), "RetryAfter must be non-negative.");
        }

        return new RetryableFailure(errorCode, safeSummary, retryAfter);
    }

    public static AssetProcessingJobOutcome Terminal(string errorCode, string safeSummary)
    {
        ValidateError(errorCode, safeSummary);
        return new TerminalFailure(errorCode, safeSummary);
    }

    public static AssetProcessingJobOutcome External(
        AssetProcessingJobOutcome inner,
        AnalysisExecutionDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(inner);
        return new ExternalExecution(inner, disposition);
    }

    /// <summary>
    /// A confirmed pre-acceptance refusal. The service proved it never started the work, so waiting
    /// for capacity must not consume an attempt: the claim is refunded instead of retried.
    /// </summary>
    public sealed class PreStartDeferral : AssetProcessingJobOutcome
    {
        public string ErrorCode { get; }

        public string SafeSummary { get; }

        /// <summary>Reservation generation that must match before the refund is applied.</summary>
        public int ReservationGeneration { get; }

        internal PreStartDeferral(string errorCode, string safeSummary, int reservationGeneration)
        {
            ErrorCode = errorCode;
            SafeSummary = safeSummary;
            ReservationGeneration = reservationGeneration;
        }

        public void Deconstruct(out string errorCode, out string safeSummary, out int reservationGeneration)
        {
            errorCode = ErrorCode;
            safeSummary = SafeSummary;
            reservationGeneration = ReservationGeneration;
        }
    }

    public static AssetProcessingJobOutcome PreStartDeferred(
        string errorCode,
        string safeSummary,
        int reservationGeneration)
    {
        ValidateError(errorCode, safeSummary);
        ArgumentOutOfRangeException.ThrowIfLessThan(reservationGeneration, 1);
        return new PreStartDeferral(errorCode, safeSummary, reservationGeneration);
    }

    private static void ValidateError(string errorCode, string safeSummary)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(errorCode);
        ArgumentException.ThrowIfNullOrWhiteSpace(safeSummary);

        if (!_errorCodeRegex.IsMatch(errorCode))
        {
            throw new ArgumentException($"ErrorCode '{errorCode}' does not match required format ^[A-Z0-9_]{{1,64}}$.", nameof(errorCode));
        }
    }

    [GeneratedRegex("^[A-Z0-9_]{1,64}$", RegexOptions.Compiled)]
    private static partial Regex MyRegex();
}

/// <summary>
/// Provider-neutral typed business handler interface for a specific job type.
/// The JobType is explicitly configured during registry/adapter registration.
/// </summary>
public interface IAssetProcessingJobHandler<TPayload, TResult>
    where TPayload : AssetProcessingPayload
    where TResult : AssetProcessingResult
{
    Task<AssetProcessingJobOutcome> Process(
        AssetProcessingJobContext<TPayload> context,
        CancellationToken cancellationToken);
}

/// <summary>
/// Narrow publisher contract for real-time SignalR state invalidation hints.
/// </summary>
public interface IAssetProcessingRealtimePublisher
{
    Task PublishJobUpdated(
        Guid ownerUserId,
        AssetProcessingUpdateMessage message,
        CancellationToken cancellationToken = default);
}
