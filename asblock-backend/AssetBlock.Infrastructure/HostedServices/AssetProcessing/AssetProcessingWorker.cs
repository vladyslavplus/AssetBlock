using System.Collections.Concurrent;
using System.Diagnostics;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.Observability;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AssetBlock.Infrastructure.HostedServices.AssetProcessing;

public sealed class AssetProcessingWorker(
    IServiceScopeFactory scopeFactory,
    IAssetProcessingJobRegistry registry,
    IAssetProcessingRealtimePublisher realtimePublisher,
    IOptions<AssetProcessingOptions> options,
    TimeProvider timeProvider,
    ILogger<AssetProcessingWorker> logger)
    : BackgroundService
{
    private static readonly TimeSpan _signalRPublishTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan _cleanupTimeout = TimeSpan.FromSeconds(5);

    private readonly AssetProcessingOptions _options = options.Value;
    private readonly string _workerId = $"worker-{Environment.MachineName}-{Guid.NewGuid():N}";

    private readonly ConcurrentDictionary<Guid, Task> _activeTasks = new();

    public int ActiveJobsCount => _activeTasks.Count;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            logger.LogInformation("AssetProcessingWorker is disabled by configuration.");
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, timeProvider, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Clean host shutdown.
            }
            return;
        }

        logger.LogInformation("AssetProcessingWorker {WorkerId} started with concurrency {Concurrency}, batch {BatchSize}.",
            _workerId, _options.Concurrency, _options.BatchSize);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RecoverExpiredLeases(stoppingToken);

                // Clean up finished tasks from tracking
                var finishedTasks = _activeTasks.Where(kvp => kvp.Value.IsCompleted).Select(kvp => kvp.Key).ToList();
                foreach (var id in finishedTasks)
                {
                    _activeTasks.TryRemove(id, out _);
                }

                var availableCapacity = _options.Concurrency - _activeTasks.Count;
                var toClaim = Math.Min(_options.BatchSize, Math.Max(0, availableCapacity));

                IReadOnlyList<ClaimedAssetProcessingJob> claimed = [];
                if (toClaim > 0)
                {
                    claimed = await ClaimBatch(toClaim, stoppingToken);
                }

                if (claimed.Count > 0)
                {
                    foreach (var job in claimed)
                    {
                        var task = RunTrackedJob(job, stoppingToken);
                        _activeTasks.TryAdd(job.JobId, task);
                    }
                }
                else
                {
                    await Task.Delay(_options.PollInterval, timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error in AssetProcessingWorker polling cycle.");
                try
                {
                    await Task.Delay(_options.PollInterval, timeProvider, stoppingToken);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        // Wait for running active tasks to complete or be interrupted during graceful shutdown
        if (!_activeTasks.IsEmpty)
        {
            logger.LogInformation("AssetProcessingWorker waiting for {Count} active tasks to finish...", _activeTasks.Count);
            try
            {
                await Task.WhenAll(_activeTasks.Values);
            }
            catch
            {
                // Individual task exceptions are logged inside RunTrackedJob
            }
        }

        logger.LogInformation("AssetProcessingWorker {WorkerId} stopped.", _workerId);
    }

    private async Task RunTrackedJob(ClaimedAssetProcessingJob job, CancellationToken stoppingToken)
    {
        try
        {
            await ProcessJob(job, stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Clean host shutdown.
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Unexpected infrastructure failure during execution of job {JobId}", job.JobId);
        }
        finally
        {
            _activeTasks.TryRemove(job.JobId, out _);
        }
    }

    private async Task<IReadOnlyList<ClaimedAssetProcessingJob>> ClaimBatch(int count, CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<IAssetProcessingJobStore>();
        return await store.ClaimPendingBatch(count, _options.LeaseDuration, _workerId, cancellationToken);
    }

    private async Task RecoverExpiredLeases(CancellationToken cancellationToken)
    {
        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var store = scope.ServiceProvider.GetRequiredService<IAssetProcessingJobStore>();
            var lifecycleStore = scope.ServiceProvider.GetRequiredService<IAssetProcessingLifecycleStore>();
            await store.RecoverExpiredLeases(cancellationToken);
            await lifecycleStore.RecoverExpiredExhaustedSecurityJobs(cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to recover expired job leases.");
        }
    }

    private async Task ProcessJob(ClaimedAssetProcessingJob job, CancellationToken hostStoppingToken)
    {
        // 1. Monotonic real-time sequence: Publish initial RUNNING state first within the per-job flow
        var initialMessage = new AssetProcessingUpdateMessage(
            job.JobId,
            job.AssetId,
            job.AssetVersionId,
            job.Type,
            AssetProcessingJobStatus.RUNNING,
            nameof(AssetProcessingJobStatus.RUNNING),
            job.UpdatedAt ?? job.CreatedAt);

        try
        {
            using var publishCts = new CancellationTokenSource(_signalRPublishTimeout);
            await realtimePublisher.PublishJobUpdated(job.OwnerUserId, initialMessage, publishCts.Token);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to publish initial real-time update for job {JobId}", job.JobId);
        }

        AssetBlockDiagnostics.IncrementActiveJobs(job.Type);
        var stopwatch = Stopwatch.StartNew();
        var finalOutcome = JobOutcomeNames.FAILED;

        Activity? activity;
        if (job.TraceParent != null && ActivityContext.TryParse(job.TraceParent, null, out var parentContext))
        {
            var links = new[] { new ActivityLink(parentContext) };
            activity = AssetBlockDiagnostics.ActivitySource.StartActivity(
                $"AssetProcessingJob {job.Type}",
                ActivityKind.Consumer,
                default(ActivityContext),
                tags: null,
                links: links);
        }
        else
        {
            activity = AssetBlockDiagnostics.ActivitySource.StartActivity(
                $"AssetProcessingJob {job.Type}",
                ActivityKind.Consumer);
        }

        if (activity != null)
        {
            activity.SetTag("job.id", job.JobId.ToString());
            activity.SetTag("asset.id", job.AssetId.ToString());
            activity.SetTag("asset.version_id", job.AssetVersionId.ToString());
            activity.SetTag("job.type", job.Type.ToString());
            activity.SetTag("job.attempt", job.AttemptCount);
        }

        using var opTimeoutCts = new CancellationTokenSource(_options.OperationTimeout, timeProvider);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(hostStoppingToken, opTimeoutCts.Token);

        var transitionSucceeded = false;

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var jobStore = scope.ServiceProvider.GetRequiredService<IAssetProcessingJobStore>();
            var lifecycleStore = scope.ServiceProvider.GetRequiredService<IAssetProcessingLifecycleStore>();

            var adapter = registry.GetHandler(job.Type);
            if (adapter is null)
            {
                logger.LogWarning("No handler is registered for job {JobId} of type {Type}", job.JobId, job.Type);

                using var cleanupCts = new CancellationTokenSource(_cleanupTimeout);
                transitionSucceeded = await FailProcessing(
                    jobStore,
                    lifecycleStore,
                    job,
                    ErrorCodes.MISSING_HANDLER,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.MISSING_HANDLER),
                    retryable: false,
                    retryDelay: TimeSpan.Zero,
                    cleanupCts.Token);

                finalOutcome = transitionSucceeded ? JobOutcomeNames.MISSING_HANDLER : JobOutcomeNames.LEASE_LOST;
                return;
            }

            AssetProcessingJobOutcome outcome;
            using var leaseLossCts = new CancellationTokenSource();
            using var executionCts = CancellationTokenSource.CreateLinkedTokenSource(linkedCts.Token, leaseLossCts.Token);
            using var renewalCts = CancellationTokenSource.CreateLinkedTokenSource(executionCts.Token);
            var renewalInterval = TimeSpan.FromTicks(_options.LeaseDuration.Ticks / 2);
            var renewalTask = Task.Run(async () =>
            {
                if (renewalInterval <= TimeSpan.Zero)
                {
                    return;
                }

                try
                {
                    using var timer = new PeriodicTimer(renewalInterval, timeProvider);
                    while (await timer.WaitForNextTickAsync(renewalCts.Token))
                    {
                        try
                        {
                            await using var renewalScope = scopeFactory.CreateAsyncScope();
                            var store = renewalScope.ServiceProvider.GetRequiredService<IAssetProcessingJobStore>();
                            var renewed = await store.RenewLease(job.JobId, job.LeaseToken, _options.LeaseDuration, renewalCts.Token);
                            if (!renewed)
                            {
                                logger.LogWarning("Failed to renew lease for job {JobId}; lease was lost", job.JobId);
                                await leaseLossCts.CancelAsync();
                                break;
                            }
                        }
                        catch (OperationCanceledException) when (renewalCts.IsCancellationRequested)
                        {
                            break;
                        }
                        catch (Exception ex)
                        {
                            logger.LogError(ex, "Error renewing lease for job {JobId}; canceling execution to prevent dual worker processing", job.JobId);
                            await leaseLossCts.CancelAsync();
                            break;
                        }
                    }
                }
                catch (OperationCanceledException) when (renewalCts.IsCancellationRequested)
                {
                    // Expected when renewal task is stopped
                }
            }, CancellationToken.None);

            try
            {
                try
                {
                    outcome = await adapter.Execute(scope.ServiceProvider, job, executionCts.Token);
                }
                finally
                {
                    await renewalCts.CancelAsync();
                    try
                    {
                        await renewalTask;
                    }
                    catch (OperationCanceledException) when (renewalCts.IsCancellationRequested)
                    {
                        // Expected on shutdown
                    }
                    catch (Exception ex)
                    {
                        logger.LogWarning(ex, "Error awaiting renewal task for job {JobId}", job.JobId);
                    }
                }
            }
            catch (OperationCanceledException) when (leaseLossCts.IsCancellationRequested)
            {
                logger.LogWarning("Job {JobId} abandoned due to lease loss or renewal failure", job.JobId);
                transitionSucceeded = false;
                finalOutcome = JobOutcomeNames.LEASE_LOST;
                return;
            }
            catch (AssetProcessingSerializerException)
            {
                if (leaseLossCts.IsCancellationRequested)
                {
                    logger.LogWarning("Job {JobId} abandoned due to lease loss; skipping serializer error transition", job.JobId);
                    transitionSucceeded = false;
                    finalOutcome = JobOutcomeNames.LEASE_LOST;
                    return;
                }

                logger.LogWarning("Invalid payload for job {JobId} of type {Type}", job.JobId, job.Type);

                using var cleanupCts = new CancellationTokenSource(_cleanupTimeout);
                transitionSucceeded = await FailProcessing(
                    jobStore, lifecycleStore, job,
                    ErrorCodes.INVALID_JOB_PAYLOAD,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.INVALID_JOB_PAYLOAD),
                    retryable: false, retryDelay: TimeSpan.Zero, cleanupCts.Token);

                finalOutcome = transitionSucceeded ? JobOutcomeNames.INVALID_PAYLOAD : JobOutcomeNames.LEASE_LOST;
                return;
            }
            catch (InvalidAssetProcessingJobResultException ex)
            {
                if (leaseLossCts.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Job {JobId} abandoned due to lease loss; skipping invalid result error transition", job.JobId);
                    transitionSucceeded = false;
                    finalOutcome = JobOutcomeNames.LEASE_LOST;
                    return;
                }

                logger.LogWarning(ex, "Invalid result returned by handler for job {JobId} of type {Type}", job.JobId, job.Type);

                using var cleanupCts = new CancellationTokenSource(_cleanupTimeout);
                transitionSucceeded = await FailProcessing(
                    jobStore, lifecycleStore, job,
                    ErrorCodes.INVALID_JOB_RESULT,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.INVALID_JOB_RESULT),
                    retryable: false, retryDelay: TimeSpan.Zero, cleanupCts.Token);

                finalOutcome = transitionSucceeded ? JobOutcomeNames.INVALID_RESULT : JobOutcomeNames.LEASE_LOST;
                return;
            }
            catch (OperationCanceledException) when (hostStoppingToken.IsCancellationRequested)
            {
                if (leaseLossCts.IsCancellationRequested)
                {
                    logger.LogWarning("Job {JobId} abandoned due to lease loss; skipping shutdown transition", job.JobId);
                    transitionSucceeded = false;
                    finalOutcome = JobOutcomeNames.LEASE_LOST;
                    return;
                }

                logger.LogInformation("Job {JobId} interrupted by worker host shutdown", job.JobId);

                using var cleanupCts = new CancellationTokenSource(_cleanupTimeout);
                transitionSucceeded = await FailProcessing(
                    jobStore, lifecycleStore, job,
                    ErrorCodes.WORKER_SHUTDOWN,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.WORKER_SHUTDOWN),
                    retryable: true, retryDelay: TimeSpan.Zero, cleanupCts.Token);

                finalOutcome = transitionSucceeded ? JobOutcomeNames.SHUTDOWN : JobOutcomeNames.LEASE_LOST;
                return;
            }
            catch (OperationCanceledException) when (opTimeoutCts.IsCancellationRequested)
            {
                if (leaseLossCts.IsCancellationRequested)
                {
                    logger.LogWarning("Job {JobId} abandoned due to lease loss; skipping timeout transition", job.JobId);
                    transitionSucceeded = false;
                    finalOutcome = JobOutcomeNames.LEASE_LOST;
                    return;
                }

                logger.LogWarning("Job {JobId} of type {Type} timed out after {Timeout}", job.JobId, job.Type, _options.OperationTimeout);

                var retryDelay = CalculateRetryDelay(job.AttemptCount, null);
                using var cleanupCts = new CancellationTokenSource(_cleanupTimeout);
                transitionSucceeded = await FailProcessing(
                    jobStore, lifecycleStore, job,
                    ErrorCodes.PROCESSING_TIMEOUT,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.PROCESSING_TIMEOUT),
                    retryable: true, retryDelay: retryDelay, cleanupCts.Token);

                finalOutcome = transitionSucceeded ? JobOutcomeNames.TIMEOUT : JobOutcomeNames.LEASE_LOST;
                return;
            }
            catch (Exception ex)
            {
                if (leaseLossCts.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "Job {JobId} abandoned due to lease loss; skipping exception transition", job.JobId);
                    transitionSucceeded = false;
                    finalOutcome = JobOutcomeNames.LEASE_LOST;
                    return;
                }

                logger.LogError(ex, "Unexpected handler exception for job {JobId} of type {Type}, attempt {Attempt}",
                    job.JobId, job.Type, job.AttemptCount);

                var retryDelay = CalculateRetryDelay(job.AttemptCount, null);
                using var cleanupCts = new CancellationTokenSource(_cleanupTimeout);
                transitionSucceeded = await FailProcessing(
                    jobStore, lifecycleStore, job,
                    ErrorCodes.PROCESSING_EXCEPTION,
                    ErrorCodesToErrorMessages.GetMessage(ErrorCodes.PROCESSING_EXCEPTION),
                    retryable: true, retryDelay: retryDelay, cleanupCts.Token);

                finalOutcome = transitionSucceeded
                    ? (job.AttemptCount >= job.MaxAttempts ? JobOutcomeNames.FAILED : JobOutcomeNames.RETRY_SCHEDULED)
                    : JobOutcomeNames.LEASE_LOST;
                return;
            }

            if (leaseLossCts.IsCancellationRequested)
            {
                logger.LogWarning("Job {JobId} finished execution after lease was lost; skipping state transition", job.JobId);
                transitionSucceeded = false;
                finalOutcome = JobOutcomeNames.LEASE_LOST;
                return;
            }

            // Map handler outcome to database transition
            using (var cleanupCts = new CancellationTokenSource(_cleanupTimeout))
            {
                if (outcome is AssetProcessingJobOutcome.Success success)
                {
                    try
                    {
                        transitionSucceeded = await jobStore.MarkSucceeded(
                            job.JobId,
                            job.LeaseToken,
                            success.Result,
                            cleanupCts.Token);

                        finalOutcome = transitionSucceeded ? JobOutcomeNames.SUCCESS : JobOutcomeNames.LEASE_LOST;
                    }
                    catch (AssetProcessingSerializerException)
                    {
                        logger.LogWarning("Invalid result serialization for job {JobId} of type {Type}", job.JobId, job.Type);

                        using var fallbackCts = new CancellationTokenSource(_cleanupTimeout);
                        transitionSucceeded = await FailProcessing(
                            jobStore, lifecycleStore, job,
                            ErrorCodes.INVALID_JOB_RESULT,
                            ErrorCodesToErrorMessages.GetMessage(ErrorCodes.INVALID_JOB_RESULT),
                            retryable: false, retryDelay: TimeSpan.Zero, fallbackCts.Token);

                        finalOutcome = transitionSucceeded ? JobOutcomeNames.INVALID_RESULT : JobOutcomeNames.LEASE_LOST;
                    }
                }
                else if (outcome is AssetProcessingJobOutcome.RetryableFailure retryable)
                {
                    var retryDelay = CalculateRetryDelay(job.AttemptCount, retryable.RetryAfter);
                    transitionSucceeded = await FailProcessing(
                        jobStore, lifecycleStore, job,
                        retryable.ErrorCode,
                        retryable.SafeSummary,
                        retryable: true, retryDelay: retryDelay, cleanupCts.Token);

                    finalOutcome = transitionSucceeded
                        ? (job.AttemptCount >= job.MaxAttempts ? JobOutcomeNames.FAILED : JobOutcomeNames.RETRY_SCHEDULED)
                        : JobOutcomeNames.LEASE_LOST;
                }
                else if (outcome is AssetProcessingJobOutcome.TerminalFailure terminal)
                {
                    transitionSucceeded = await FailProcessing(
                        jobStore, lifecycleStore, job,
                        terminal.ErrorCode,
                        terminal.SafeSummary,
                        retryable: false, retryDelay: TimeSpan.Zero, cleanupCts.Token);

                    finalOutcome = transitionSucceeded ? JobOutcomeNames.FAILED : JobOutcomeNames.LEASE_LOST;
                }
                else if (outcome is AssetProcessingJobOutcome.AtomicCommitted committed)
                {
                    // Lifecycle store already committed transition atomically.
                    // Skip second DB round-trip; proceed directly to SignalR publish.
                    transitionSucceeded = true;
                    finalOutcome = committed.JobStatus == AssetProcessingJobStatus.SUCCEEDED ? JobOutcomeNames.SUCCESS : JobOutcomeNames.FAILED;
                }
            }
        }
        finally
        {
            // 2. Monotonic real-time sequence: Publish final state update sequentially after committed DB transition
            if (transitionSucceeded)
            {
                try
                {
                    using var stateCts = new CancellationTokenSource(_cleanupTimeout);
                    await using var scope = scopeFactory.CreateAsyncScope();
                    var store = scope.ServiceProvider.GetRequiredService<IAssetProcessingJobStore>();
                    var state = await store.GetRealtimeState(job.JobId, stateCts.Token);
                    if (state != null)
                    {
                        using var finalPublishCts = new CancellationTokenSource(_signalRPublishTimeout);
                        await realtimePublisher.PublishJobUpdated(state.OwnerUserId, state.ToClientMessage(), finalPublishCts.Token);
                    }
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to publish SignalR realtime update for job {JobId}", job.JobId);
                }
            }

            AssetBlockDiagnostics.DecrementActiveJobs(job.Type);
            activity?.Dispose();

            var duration = stopwatch.Elapsed;
            var queueAge = timeProvider.GetUtcNow() - job.CreatedAt;
            AssetBlockDiagnostics.RecordJobCompletion(job.Type, finalOutcome, duration, queueAge, job.AttemptCount);
        }
    }

    private static async Task<bool> FailProcessing(
        IAssetProcessingJobStore jobStore,
        IAssetProcessingLifecycleStore lifecycleStore,
        ClaimedAssetProcessingJob job,
        string errorCode,
        string safeSummary,
        bool retryable,
        TimeSpan retryDelay,
        CancellationToken cancellationToken)
    {
        var lastAttempt = job.AttemptCount >= job.MaxAttempts;
        if (!retryable || lastAttempt)
        {
            if (job.Type is AssetProcessingJobType.ARCHIVE_INSPECTION or AssetProcessingJobType.MALWARE_SCAN)
            {
                return await lifecycleStore.TransitionProcessingFailed(
                    job.JobId,
                    job.LeaseToken,
                    job.AssetId,
                    job.AssetVersionId,
                    job.Type,
                    errorCode,
                    safeSummary,
                    cancellationToken);
            }

            return await jobStore.MarkFailedTerminal(
                job.JobId,
                job.LeaseToken,
                errorCode,
                safeSummary,
                cancellationToken);
        }

        return await jobStore.MarkFailedRetryable(
            job.JobId,
            job.LeaseToken,
            errorCode,
            safeSummary,
            retryDelay,
            cancellationToken);
    }

    public TimeSpan CalculateRetryDelay(int attemptCount, TimeSpan? handlerRetryAfter)
    {
        var exponent = Math.Max(0, attemptCount - 1);
        var multiplier = Math.Pow(2, Math.Min(30, exponent));
        var exponentialTicks = (long)(_options.InitialRetryDelay.Ticks * multiplier);
        var exponentialDelay = TimeSpan.FromTicks(exponentialTicks);

        var delay = handlerRetryAfter.HasValue && handlerRetryAfter.Value > exponentialDelay
            ? handlerRetryAfter.Value
            : exponentialDelay;

        return delay > _options.MaxRetryDelay ? _options.MaxRetryDelay : delay;
    }
}
