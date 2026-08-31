using System.Diagnostics;
using System.Diagnostics.Metrics;
using AssetBlock.Domain.Core.Enums;

namespace AssetBlock.Infrastructure.Observability;

public static class AssetBlockDiagnostics
{
    public const string ACTIVITY_SOURCE_NAME = "AssetBlock.Backend";
    public const string METER_NAME = "AssetBlock.Backend";

    public static readonly ActivitySource ActivitySource = new(ACTIVITY_SOURCE_NAME);
    private static readonly Meter _meter = new(METER_NAME);

    // --- Outbox Processing ---
    private static readonly Histogram<double> _outboxProcessingDuration = _meter.CreateHistogram<double>(
        "assetblock.outbox.processing.duration",
        unit: "s",
        description: "Duration of outbox message processing");

    private static readonly Counter<long> _outboxProcessingCount = _meter.CreateCounter<long>(
        "assetblock.outbox.processing.count",
        description: "Count of processed outbox messages by outcome");

    public static void RecordOutboxProcessing(TimeSpan elapsed, string outboxType, DiagnosticsOutcome outcome)
    {
        var tags = new TagList
        {
            { "outbox.type", outboxType },
            { "outbox.outcome", outcome.ToTagValue() }
        };
        _outboxProcessingDuration.Record(elapsed.TotalSeconds, in tags);
        _outboxProcessingCount.Add(1, in tags);
    }

    // --- Analytics Aggregation ---
    private static readonly Histogram<double> _analyticsAggregationDuration = _meter.CreateHistogram<double>(
        "assetblock.analytics.aggregation.duration",
        unit: "s",
        description: "Duration of analytics aggregation iterations");

    public static void RecordAnalyticsAggregation(TimeSpan elapsed, DiagnosticsOutcome outcome)
    {
        var tags = new TagList
        {
            { "analytics.outcome", outcome.ToTagValue() }
        };
        _analyticsAggregationDuration.Record(elapsed.TotalSeconds, in tags);
    }

    // --- Storage Orphan Cleanup ---
    private static readonly Histogram<double> _orphanCleanupDuration = _meter.CreateHistogram<double>(
        "assetblock.storage.orphan_cleanup.duration",
        unit: "s",
        description: "Duration of storage orphan cleanup cycles");

    private static readonly Counter<long> _orphanCleanupDeleted = _meter.CreateCounter<long>(
        "assetblock.storage.orphan_cleanup.deleted",
        description: "Count of successfully deleted orphan storage objects");

    private static readonly Counter<long> _orphanCleanupFailures = _meter.CreateCounter<long>(
        "assetblock.storage.orphan_cleanup.failures",
        description: "Count of errors encountered during storage orphan cleanup");

    public static void RecordOrphanCleanup(TimeSpan elapsed, DiagnosticsOutcome outcome, int deletedCount, int failedCount)
    {
        var tags = new TagList
        {
            { "cleanup.outcome", outcome.ToTagValue() }
        };
        _orphanCleanupDuration.Record(elapsed.TotalSeconds, in tags);

        if (deletedCount > 0)
        {
            _orphanCleanupDeleted.Add(deletedCount);
        }
        if (failedCount > 0)
        {
            _orphanCleanupFailures.Add(failedCount);
        }
    }

    // --- Email Dispatch ---
    private static readonly Histogram<double> _emailDispatchDuration = _meter.CreateHistogram<double>(
        "assetblock.email.dispatch.duration",
        unit: "s",
        description: "Duration of email dispatch attempts");

    public static void RecordEmailDispatch(TimeSpan elapsed, EmailTemplateKind template, DiagnosticsOutcome outcome)
    {
        var tags = new TagList
        {
            { "email.template", template.ToString() },
            { "email.outcome", outcome.ToTagValue() }
        };
        _emailDispatchDuration.Record(elapsed.TotalSeconds, in tags);
    }

    // --- Asset Processing Jobs ---
    private static readonly UpDownCounter<long> _activeJobsCount = _meter.CreateUpDownCounter<long>(
        "assetblock.jobs.active",
        description: "Number of currently active asset processing jobs");

    private static readonly Counter<long> _completedJobsCount = _meter.CreateCounter<long>(
        "assetblock.jobs.completed",
        description: "Count of completed asset processing jobs by outcome");

    private static readonly Histogram<double> _jobDuration = _meter.CreateHistogram<double>(
        "assetblock.jobs.duration",
        unit: "s",
        description: "Duration of asset processing job execution in seconds");

    private static readonly Histogram<double> _jobQueueAge = _meter.CreateHistogram<double>(
        "assetblock.jobs.queue_age",
        unit: "s",
        description: "Age of asset processing job in queue before execution in seconds");

    private static readonly Histogram<long> _jobAttempts = _meter.CreateHistogram<long>(
        "assetblock.jobs.attempts",
        description: "Number of execution attempts for asset processing jobs");

    public static void IncrementActiveJobs(AssetProcessingJobType type)
    {
        var tags = new TagList { { "job.type", type.ToString() } };
        _activeJobsCount.Add(1, in tags);
    }

    public static void DecrementActiveJobs(AssetProcessingJobType type)
    {
        var tags = new TagList { { "job.type", type.ToString() } };
        _activeJobsCount.Add(-1, in tags);
    }

    public static void RecordJobCompletion(
        AssetProcessingJobType type,
        string outcome,
        TimeSpan duration,
        TimeSpan queueAge,
        int attemptCount)
    {
        var tags = new TagList
        {
            { "job.type", type.ToString() },
            { "job.outcome", outcome }
        };

        _completedJobsCount.Add(1, in tags);
        _jobDuration.Record(duration.TotalSeconds, in tags);
        _jobQueueAge.Record(Math.Max(0.0, queueAge.TotalSeconds), in tags);
        _jobAttempts.Record(attemptCount, in tags);
    }

    private static readonly Histogram<double> _scanDuration = _meter.CreateHistogram<double>(
        "assetblock.scan.duration",
        unit: "s",
        description: "Duration of ClamAV INSTREAM scans");

    private static readonly Histogram<long> _scanBytes = _meter.CreateHistogram<long>(
        "assetblock.scan.bytes",
        unit: "By",
        description: "ClamAV scan byte counts by kind");

    private static readonly Counter<long> _scanResults = _meter.CreateCounter<long>(
        "assetblock.scan.results",
        description: "Count of ClamAV scans by outcome");

    private static readonly Lock _signatureAgeGate = new();
    private static DateTimeOffset? _signatureDatabaseBuiltAt;
    internal static TimeProvider TimeProvider { get; set; } = TimeProvider.System;

    private static readonly ObservableGauge<double> _signatureAge = _meter.CreateObservableGauge(
        "assetblock.scan.signature_age",
        ObserveSignatureAgeMeasurements,
        unit: "s",
        description: "Age of the last successfully parsed ClamAV signature database");

    internal static void RecordScan(
        TimeSpan elapsed,
        ScanDiagnosticsOutcome outcome,
        long bytesRead,
        long bytesAttempted,
        long bytesTransferred)
    {
        var tags = new TagList { { "scan.outcome", ToTagValue(outcome) } };
        _scanDuration.Record(Math.Max(0.0, elapsed.TotalSeconds), in tags);
        _scanResults.Add(1, in tags);
        RecordScanBytes(bytesRead, ScanByteKind.READ);
        RecordScanBytes(bytesAttempted, ScanByteKind.ATTEMPTED);
        RecordScanBytes(bytesTransferred, ScanByteKind.TRANSFERRED);
    }

    internal static void ObserveSignatureDatabase(DateTimeOffset builtAtUtc)
    {
        _ = _signatureAge;
        lock (_signatureAgeGate)
        {
            _signatureDatabaseBuiltAt = builtAtUtc.ToUniversalTime();
        }
    }

    private static void RecordScanBytes(long bytes, ScanByteKind kind)
    {
        var tags = new TagList { { "scan.bytes.kind", ToTagValue(kind) } };
        _scanBytes.Record(Math.Max(0L, bytes), in tags);
    }

    private static IEnumerable<Measurement<double>> ObserveSignatureAgeMeasurements()
    {
        DateTimeOffset builtAt;
        lock (_signatureAgeGate)
        {
            if (_signatureDatabaseBuiltAt is not { } stored)
            {
                return [];
            }

            builtAt = stored;
        }

        TimeSpan age = TimeProvider.GetUtcNow() - builtAt;
        if (age < TimeSpan.Zero)
        {
            age = TimeSpan.Zero;
        }

        return [new Measurement<double>(age.TotalSeconds)];
    }

    private static string ToTagValue(ScanDiagnosticsOutcome outcome) => outcome switch
    {
        ScanDiagnosticsOutcome.CLEAN => "CLEAN",
        ScanDiagnosticsOutcome.INFECTED => "INFECTED",
        ScanDiagnosticsOutcome.LIMIT_EXCEEDED => "LIMIT_EXCEEDED",
        ScanDiagnosticsOutcome.UNAVAILABLE => "UNAVAILABLE",
        ScanDiagnosticsOutcome.ERROR => "ERROR",
        ScanDiagnosticsOutcome.CANCELLED => "CANCELLED",
        _ => "ERROR"
    };

    private static string ToTagValue(ScanByteKind kind) => kind switch
    {
        ScanByteKind.READ => "READ",
        ScanByteKind.ATTEMPTED => "ATTEMPTED",
        ScanByteKind.TRANSFERRED => "TRANSFERRED",
        _ => "READ"
    };

    private static readonly Counter<long> _aiRequests = _meter.CreateCounter<long>(
        "assetblock.ai.requests",
        description: "Count of AI generation attempts");

    private static readonly Counter<long> _aiResults = _meter.CreateCounter<long>(
        "assetblock.ai.results",
        description: "Count of AI generation outcomes");

    private static readonly Histogram<double> _aiDuration = _meter.CreateHistogram<double>(
        "assetblock.ai.duration",
        unit: "s",
        description: "Duration of AI generation attempts");

    private static readonly Histogram<long> _aiInputTokens = _meter.CreateHistogram<long>(
        "assetblock.ai.input_tokens",
        description: "Prompt token counts reported by an AI provider");

    private static readonly Histogram<long> _aiOutputTokens = _meter.CreateHistogram<long>(
        "assetblock.ai.output_tokens",
        description: "Completion token counts reported by an AI provider");

    internal static void RecordAiGeneration(
        AiProviderKind? provider,
        string modelTag,
        AiDiagnosticsOutcome outcome,
        TimeSpan duration,
        int? inputTokens,
        int? outputTokens)
    {
        var tags = new TagList
        {
            { "ai.provider", provider is { } kind ? kind.ToString() : "UNKNOWN" },
            { "ai.model", modelTag },
            { "ai.outcome", ToTagValue(outcome) }
        };

        _aiRequests.Add(1, in tags);
        _aiResults.Add(1, in tags);
        _aiDuration.Record(Math.Max(0.0, duration.TotalSeconds), in tags);
        if (inputTokens is { } input)
        {
            _aiInputTokens.Record(Math.Max(0L, input), in tags);
        }

        if (outputTokens is { } output)
        {
            _aiOutputTokens.Record(Math.Max(0L, output), in tags);
        }
    }

    private static string ToTagValue(AiDiagnosticsOutcome outcome) => outcome switch
    {
        AiDiagnosticsOutcome.SUCCESS => "SUCCESS",
        AiDiagnosticsOutcome.DISABLED => "DISABLED",
        AiDiagnosticsOutcome.RETRYABLE => "RETRYABLE",
        AiDiagnosticsOutcome.TERMINAL => "TERMINAL",
        AiDiagnosticsOutcome.CANCELLED => "CANCELLED",
        _ => "TERMINAL"
    };
}

public static class JobOutcomeNames
{
    public const string SUCCESS = "SUCCESS";
    public const string RETRY_SCHEDULED = "RETRY_SCHEDULED";
    public const string FAILED = "FAILED";
    public const string TIMEOUT = "TIMEOUT";
    public const string MISSING_HANDLER = "MISSING_HANDLER";
    public const string INVALID_PAYLOAD = "INVALID_PAYLOAD";
    public const string INVALID_RESULT = "INVALID_RESULT";
    public const string LEASE_LOST = "LEASE_LOST";
    public const string SHUTDOWN = "SHUTDOWN";
}
