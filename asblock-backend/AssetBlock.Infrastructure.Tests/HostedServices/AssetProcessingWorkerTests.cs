using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.HostedServices.AssetProcessing;
using AssetBlock.Infrastructure.Persistence.Stores;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

public sealed class AssetProcessingWorkerTests
{
    private readonly IServiceScopeFactory _scopeFactory = Substitute.For<IServiceScopeFactory>();
    private readonly IAssetProcessingJobRegistry _registry = Substitute.For<IAssetProcessingJobRegistry>();
    private readonly IAssetProcessingRealtimePublisher _publisher = Substitute.For<IAssetProcessingRealtimePublisher>();
    private readonly IAssetProcessingJobStore _store = Substitute.For<IAssetProcessingJobStore>();
    private readonly IAssetProcessingLifecycleStore _lifecycleStore = Substitute.For<IAssetProcessingLifecycleStore>();
    private readonly IAnalysisCapacityStore _capacityStore = Substitute.For<IAnalysisCapacityStore>();
    private readonly IAnalysisReadinessGate _gate = Substitute.For<IAnalysisReadinessGate>();
    private readonly ICodeAnalysisReportFinalizer _codeAnalysisFinalizer = Substitute.For<ICodeAnalysisReportFinalizer>();
    private readonly TimeProvider _timeProvider = TimeProvider.System;

    private static AssetProcessingOptions CreateDefaultOptions(bool enabled = true) => new()
    {
        Enabled = enabled,
        PollInterval = TimeSpan.FromMilliseconds(50),
        BatchSize = 5,
        Concurrency = 5,
        LeaseDuration = TimeSpan.FromMinutes(2),
        OperationTimeout = TimeSpan.FromMinutes(1),
        MaxAttempts = 3,
        InitialRetryDelay = TimeSpan.FromSeconds(10),
        MaxRetryDelay = TimeSpan.FromMinutes(5)
    };

    private AssetProcessingWorker CreateWorker(
        AssetProcessingOptions options,
        Func<double>? jitterProvider = null,
        CodeAnalysisOptions? analysisOptions = null)
    {
        IServiceProvider serviceProvider = Substitute.For<IServiceProvider>();
        IServiceScope scope = Substitute.For<IServiceScope>();

        serviceProvider.GetService(typeof(IAssetProcessingJobStore)).Returns(_store);
        serviceProvider.GetService(typeof(IAssetProcessingLifecycleStore)).Returns(_lifecycleStore);
        serviceProvider.GetService(typeof(IAssetProcessingJobRegistry)).Returns(_registry);

        // Analysis jobs are claimed only through the shared capacity reservation.
        serviceProvider.GetService(typeof(IAnalysisCapacityStore)).Returns(_capacityStore);
        serviceProvider.GetService(typeof(IAnalysisReadinessGate)).Returns(_gate);
        serviceProvider.GetService(typeof(ICodeAnalysisReportFinalizer)).Returns(_codeAnalysisFinalizer);
        scope.ServiceProvider.Returns(serviceProvider);
        _scopeFactory.CreateScope().Returns(scope);

        return new AssetProcessingWorker(
            _scopeFactory,
            _publisher,
            Microsoft.Extensions.Options.Options.Create(options),
            Microsoft.Extensions.Options.Options.Create(analysisOptions ?? new CodeAnalysisOptions()),
            _timeProvider,
            NullLogger<AssetProcessingWorker>.Instance,
            jitterProvider ?? (() => 0.5));
    }

    [Theory]
    [InlineData(1, 10)]   // 10s * 2^0 = 10s
    [InlineData(2, 20)]   // 10s * 2^1 = 20s
    [InlineData(3, 40)]   // 10s * 2^2 = 40s
    [InlineData(4, 80)]   // 10s * 2^3 = 80s
    [InlineData(10, 300)] // Capped at MaxRetryDelay (300s)
    public void CalculateRetryDelay_ExponentialBackoff_CalculatesCorrectDelay(int attempt, int expectedSeconds)
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        TimeSpan delay = worker.CalculateRetryDelay(attempt, null);

        delay.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void CalculateRetryDelay_WhenHandlerProvidesLargerRetryAfter_UsesHandlerValue()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var handlerRetryAfter = TimeSpan.FromMinutes(2); // 120s > 10s exponential
        TimeSpan delay = worker.CalculateRetryDelay(1, handlerRetryAfter);

        delay.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void CalculateRetryDelay_WhenHandlerProvidesValueExceedingMax_CapsAtMaxRetryDelay()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var handlerRetryAfter = TimeSpan.FromHours(1); // 1h > 5min max
        TimeSpan delay = worker.CalculateRetryDelay(1, handlerRetryAfter);

        delay.Should().Be(options.MaxRetryDelay);
    }

    [Fact]
    public async Task Worker_WhenRecoveringLeases_AlsoRecoversExhaustedSecurityJobs()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var recoveredTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]));
        _store.RecoverExpiredLeases(Arg.Any<CancellationToken>()).Returns(0);
        _lifecycleStore.RecoverExpiredExhaustedSecurityJobs(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            recoveredTcs.TrySetResult(true);
            return Task.FromResult(0);
        });

        await worker.StartAsync(CancellationToken.None);
        await recoveredTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        await _store.Received().RecoverExpiredLeases(Arg.Any<CancellationToken>());
        await _lifecycleStore.Received().RecoverExpiredExhaustedSecurityJobs(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenDisabled_DoesNotClaimJobs()
    {
        AssetProcessingOptions options = CreateDefaultOptions(enabled: false);
        AssetProcessingWorker worker = CreateWorker(options);

        await worker.StartAsync(CancellationToken.None);
        await worker.StopAsync(CancellationToken.None);

        await _store.DidNotReceive().ClaimPendingBatch(
            Arg.Any<int>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenAnalysisNotReady_SkipsAnalysisClaimsButRecoversAndServesGeneralJobs()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options, analysisOptions: new CodeAnalysisOptions { Enabled = true });

        _gate.EnsureReady(Arg.Any<CancellationToken>()).Returns(false);

        var generalJobId = Guid.NewGuid();
        var generalLease = Guid.NewGuid();
        var generalJob = new ClaimedAssetProcessingJob(
            generalJobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            "{}",
            null,
            generalLease,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var callCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([generalJob]);
                }
                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns(AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(0, 0)));
        _registry.GetHandler(AssetProcessingJobType.ARCHIVE_INSPECTION).Returns(adapter);
        _store.MarkSucceeded(
                generalJobId, generalLease, Arg.Any<AssetProcessingResult>(), Arg.Any<CancellationToken>())
            .Returns(true);
        _capacityStore.RecoverExpiredReservations(Arg.Any<CancellationToken>()).Returns(0);

        var generalDoneTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.MarkSucceeded(
                Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingResult>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                generalDoneTcs.TrySetResult(true);
                return Task.FromResult(true);
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await generalDoneTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            // Let at least one recovery pass run while the general job completes.
            await Task.Delay(200);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        // No new analysis claim may burn attempts while readiness is closed ...
        await _capacityStore.DidNotReceive().ClaimWithCapacity(
            Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
        // ... but occupied reservations are still reconciled and unrelated jobs flow.
        await _capacityStore.Received().RecoverExpiredReservations(Arg.Any<CancellationToken>());
        await _store.Received().ClaimPendingBatch(
            Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenAnalysisHandlerReportsDeterministicPreStartRefusal_FailsBoundRunWithoutOwnerRetry()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options, analysisOptions: new CodeAnalysisOptions { Enabled = true });
        var jobId = Guid.NewGuid();
        var assetId = Guid.NewGuid();
        var versionId = Guid.NewGuid();
        var runId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        string transportToken = new('a', 64);
        var claimed = new ClaimedAssetProcessingJob(
            jobId, assetId, versionId, Guid.NewGuid(), AssetProcessingJobType.CODE_ANALYSIS,
            1, 1, 3, "{}", null, leaseToken, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow, runId, TransportLeaseToken: transportToken);
        _gate.EnsureReady(Arg.Any<CancellationToken>()).Returns(true);
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]));
        _store.RecoverExpiredLeases(Arg.Any<CancellationToken>()).Returns(0);
        _capacityStore.RecoverExpiredReservations(Arg.Any<CancellationToken>()).Returns(0);
        var claimCount = 0;
        _capacityStore.ClaimWithCapacity(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => Interlocked.Increment(ref claimCount) == 1
                ? Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimed])
                : Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]));
        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(claimed, Arg.Any<CancellationToken>()).Returns(AssetProcessingJobOutcome.External(
            AssetProcessingJobOutcome.Terminal(ErrorCodes.ANALYSIS_RESPONSE_INVALID,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ANALYSIS_RESPONSE_INVALID)),
            AnalysisExecutionDisposition.NOT_STARTED));
        _registry.GetHandler(AssetProcessingJobType.CODE_ANALYSIS).Returns(adapter);
        var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _codeAnalysisFinalizer.FinalizeFailure(jobId, leaseToken, Arg.Any<string>(), Arg.Any<string>(), false,
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                completed.TrySetResult(true);
                return Task.FromResult(AssetProcessingJobOutcome.CommittedFailed());
            });
        _capacityStore.ReleaseForTerminalJob(jobId, transportToken, AnalysisExecutionDisposition.NOT_STARTED,
            Arg.Any<CancellationToken>()).Returns(true);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await completed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await _codeAnalysisFinalizer.Received(1).FinalizeFailure(jobId, leaseToken,
            ErrorCodes.ANALYSIS_RESPONSE_INVALID, Arg.Any<string>(), false, Arg.Any<CancellationToken>());
        await _capacityStore.Received(1).ReleaseForTerminalJob(
            jobId, transportToken, AnalysisExecutionDisposition.NOT_STARTED, Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedTerminal(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenReadinessIsProvenAfterStartup_ShouldOpenAnalysisClaimsWithoutRestart()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options, analysisOptions: new CodeAnalysisOptions { Enabled = true });

        // Closed on the first evaluation, exactly as it is before the approved policy manifest is
        // published, and proven afterwards without the worker being restarted.
        var evaluations = 0;
        _gate.EnsureReady(Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromResult(Interlocked.Increment(ref evaluations) >= 2));
        _capacityStore.RecoverExpiredReservations(Arg.Any<CancellationToken>()).Returns(0);
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]));
        _capacityStore.ClaimWithCapacity(
                Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]));

        var claimedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _capacityStore
            .When(c => c.ClaimWithCapacity(
                Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(),
                Arg.Any<string>(), Arg.Any<CancellationToken>()))
            .Do(_ => claimedTcs.TrySetResult(true));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            // Readiness is re-evaluated on the claim loop, so the recovered deployment opens claims
            // on its own within one poll interval.
            await claimedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await _gate.Received(2).EnsureReady(Arg.Any<CancellationToken>());
        await _capacityStore.Received().ClaimWithCapacity(
            Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(), Arg.Any<TimeSpan>(),
            Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenHandlerThrowsInvalidResultException_MarksTerminalWithoutRetry()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ownerUserId,
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<AssetProcessingJobOutcome>(_ => throw new InvalidAssetProcessingJobResultException("Result type mismatch"));

        _registry.GetHandler(AssetProcessingJobType.ARCHIVE_INSPECTION).Returns(adapter);

        var callCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }
                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        var transitionTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lifecycleStore.TransitionProcessingFailed(jobId, leaseToken, claimedJob.AssetId, claimedJob.AssetVersionId, AssetProcessingJobType.ARCHIVE_INSPECTION, "INVALID_JOB_RESULT", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                transitionTcs.TrySetResult(true);
                return Task.FromResult(true);
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await transitionTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await _lifecycleStore.Received(1).TransitionProcessingFailed(
            jobId,
            leaseToken,
            claimedJob.AssetId,
            claimedJob.AssetVersionId,
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            "INVALID_JOB_RESULT",
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());

        await _store.DidNotReceive().MarkFailedRetryable(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenDbTransitionThrows_LogsAndRemovesTaskWithoutDoubleTransition()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 10))));

        _registry.GetHandler(AssetProcessingJobType.ARCHIVE_INSPECTION).Returns(adapter);

        var callCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }
                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        var dbErrorTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.MarkSucceeded(jobId, leaseToken, Arg.Any<AssetProcessingResult>(), Arg.Any<CancellationToken>())
            .Returns<bool>(_ =>
            {
                dbErrorTcs.TrySetResult(true);
                throw new InvalidOperationException("PostgreSQL connection severed");
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await dbErrorTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await WaitUntil(() => worker.ActiveJobsCount == 0);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        worker.ActiveJobsCount.Should().Be(0);

        // Verify it did not attempt a fallback retry or terminal transition
        await _store.DidNotReceive().MarkFailedRetryable(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());

        await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<AssetProcessingJobType>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_SignalRUpdates_SentInMonotonicOrderWithJobUpdatedAt()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow.AddSeconds(-1);

        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ownerUserId,
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            updatedAt);

        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 10))));

        _registry.GetHandler(AssetProcessingJobType.ARCHIVE_INSPECTION).Returns(adapter);

        var callCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }
                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        _store.MarkSucceeded(jobId, leaseToken, Arg.Any<AssetProcessingResult>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        var realtimeState = new AssetProcessingJobRealtimeState(
            jobId,
            claimedJob.AssetId,
            claimedJob.AssetVersionId,
            ownerUserId,
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            AssetProcessingJobStatus.SUCCEEDED,
            "SUCCEEDED",
            DateTimeOffset.UtcNow);

        _store.GetRealtimeState(jobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AssetProcessingJobRealtimeState?>(realtimeState));

        var publishedMessages = new List<AssetProcessingUpdateMessage>();
        var publishedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _publisher.PublishJobUpdated(ownerUserId, Arg.Do<AssetProcessingUpdateMessage>(msg =>
        {
            publishedMessages.Add(msg);
            if (publishedMessages.Count >= 2)
            {
                publishedTcs.TrySetResult(true);
            }
        }), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await publishedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        publishedMessages.Should().HaveCount(2);
        publishedMessages[0].Status.Should().Be(AssetProcessingJobStatus.RUNNING);
        publishedMessages[0].UpdatedAt.Should().Be(updatedAt);
        publishedMessages[1].Status.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
    }

    [Fact]
    public async Task Worker_WhenEmbeddingGenerationJob_DoesNotPublishSignalRUpdates()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        DateTimeOffset updatedAt = DateTimeOffset.UtcNow;

        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ownerUserId,
            AssetProcessingJobType.EMBEDDING_GENERATION,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow.AddMinutes(-5),
            updatedAt);

        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        var executedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                executedTcs.TrySetResult(true);
                return Task.FromResult(AssetProcessingJobOutcome.CommittedSucceeded());
            });

        _registry.GetHandler(AssetProcessingJobType.EMBEDDING_GENERATION).Returns(adapter);

        var callCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }
                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await executedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
            await Task.Delay(50);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await _publisher.DidNotReceive().PublishJobUpdated(
            Arg.Any<Guid>(),
            Arg.Any<AssetProcessingUpdateMessage>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenListingCopilotCommitsAtomically_ShouldPublishFinalStateOnceWithoutMarkSucceeded()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            ownerUserId,
            AssetProcessingJobType.LISTING_COPILOT,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(AssetProcessingJobOutcome.CommittedSucceeded()));

        _registry.GetHandler(AssetProcessingJobType.LISTING_COPILOT).Returns(adapter);

        var callCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }

                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        var realtimeState = new AssetProcessingJobRealtimeState(
            jobId,
            claimedJob.AssetId,
            claimedJob.AssetVersionId,
            ownerUserId,
            AssetProcessingJobType.LISTING_COPILOT,
            AssetProcessingJobStatus.SUCCEEDED,
            "SUCCEEDED",
            DateTimeOffset.UtcNow);

        _store.GetRealtimeState(jobId, Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<AssetProcessingJobRealtimeState?>(realtimeState));

        var finalPublishCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var publishedMessages = new List<AssetProcessingUpdateMessage>();
        _publisher.PublishJobUpdated(
                ownerUserId,
                Arg.Do<AssetProcessingUpdateMessage>(message =>
                {
                    publishedMessages.Add(message);
                    if (message.Status == AssetProcessingJobStatus.SUCCEEDED)
                    {
                        finalPublishCompleted.TrySetResult(true);
                    }
                }),
                Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await finalPublishCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await _store.DidNotReceive().MarkSucceeded(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<AssetProcessingResult>(),
            Arg.Any<CancellationToken>());
        publishedMessages.Should().HaveCount(2);
        publishedMessages[1].Status.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
        publishedMessages.Count(m => m.Status == AssetProcessingJobStatus.SUCCEEDED).Should().Be(1);
    }

    [Fact]
    public async Task Worker_WhenRetryableExceptionBeforeFinalAttempt_MarksRetryableWithoutFailingVersion()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<AssetProcessingJobOutcome>(_ => throw new InvalidOperationException("transient storage"));

        _registry.GetHandler(AssetProcessingJobType.ARCHIVE_INSPECTION).Returns(adapter);

        var callCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }

                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        var retryableTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.MarkFailedRetryable(
                jobId,
                leaseToken,
                ErrorCodes.PROCESSING_EXCEPTION,
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                retryableTcs.TrySetResult(true);
                return Task.FromResult(true);
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await retryableTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await _store.Received(1).MarkFailedRetryable(
            jobId,
            leaseToken,
            ErrorCodes.PROCESSING_EXCEPTION,
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
        await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<AssetProcessingJobType>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenScannerUnavailableBeforeFinalAttempt_MarksRetryable()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.MALWARE_SCAN,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns(AssetProcessingJobOutcome.Retryable(
                ErrorCodes.SCANNER_UNAVAILABLE,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SCANNER_UNAVAILABLE)));

        _registry.GetHandler(AssetProcessingJobType.MALWARE_SCAN).Returns(adapter);

        var callCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }

                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        var retryableTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.MarkFailedRetryable(
                jobId,
                leaseToken,
                ErrorCodes.SCANNER_UNAVAILABLE,
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                retryableTcs.TrySetResult(true);
                return Task.FromResult(true);
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await retryableTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await _store.Received(1).MarkFailedRetryable(
            jobId,
            leaseToken,
            ErrorCodes.SCANNER_UNAVAILABLE,
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
        await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<AssetProcessingJobType>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenScannerUnavailableOnFinalAttempt_TransitionsProcessingFailed()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.MALWARE_SCAN,
            1,
            3,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns(AssetProcessingJobOutcome.Retryable(
                ErrorCodes.SCANNER_UNAVAILABLE,
                ErrorCodesToErrorMessages.GetMessage(ErrorCodes.SCANNER_UNAVAILABLE)));

        _registry.GetHandler(AssetProcessingJobType.MALWARE_SCAN).Returns(adapter);

        var callCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }

                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        var failedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lifecycleStore.TransitionProcessingFailed(
                jobId,
                leaseToken,
                claimedJob.AssetId,
                claimedJob.AssetVersionId,
                AssetProcessingJobType.MALWARE_SCAN,
                ErrorCodes.SCANNER_UNAVAILABLE,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                failedTcs.TrySetResult(true);
                return Task.FromResult(true);
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await failedTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await _lifecycleStore.Received(1).TransitionProcessingFailed(
            jobId,
            leaseToken,
            claimedJob.AssetId,
            claimedJob.AssetVersionId,
            AssetProcessingJobType.MALWARE_SCAN,
            ErrorCodes.SCANNER_UNAVAILABLE,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedRetryable(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("timeout")]
    [InlineData("exception")]
    [InlineData("payload")]
    [InlineData("missing")]
    public async Task Worker_WhenFinalAttemptFails_TransitionsProcessingFailed(string mode)
    {
        var options = new AssetProcessingOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(50),
            BatchSize = 5,
            Concurrency = 5,
            LeaseDuration = TimeSpan.FromMinutes(2),
            OperationTimeout = TimeSpan.FromMilliseconds(30),
            MaxAttempts = 3,
            InitialRetryDelay = TimeSpan.FromSeconds(10),
            MaxRetryDelay = TimeSpan.FromMinutes(5)
        };
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.MALWARE_SCAN,
            1,
            3,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        if (mode == "timeout")
        {
            adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
                .Returns(ci => TimeoutThenSucceed(ci.Arg<CancellationToken>()));
        }
        else if (mode == "payload")
        {
            adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
                .Returns<AssetProcessingJobOutcome>(_ => throw new AssetProcessingSerializerException("bad payload"));
        }
        else
        {
            adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
                .Returns<AssetProcessingJobOutcome>(_ => throw new InvalidOperationException("boom"));
        }

        if (mode == "missing")
        {
            _registry.GetHandler(AssetProcessingJobType.MALWARE_SCAN).Returns((IAssetProcessingJobHandlerAdapter?)null);
        }
        else
        {
            _registry.GetHandler(AssetProcessingJobType.MALWARE_SCAN).Returns(adapter);
        }

        var callCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }

                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        var transitionCompleted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _lifecycleStore.TransitionProcessingFailed(
                jobId,
                leaseToken,
                claimedJob.AssetId,
                claimedJob.AssetVersionId,
                AssetProcessingJobType.MALWARE_SCAN,
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                transitionCompleted.TrySetResult(true);
                return Task.FromResult(true);
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await transitionCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await _lifecycleStore.Received().TransitionProcessingFailed(
            jobId,
            leaseToken,
            claimedJob.AssetId,
            claimedJob.AssetVersionId,
            AssetProcessingJobType.MALWARE_SCAN,
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedRetryable(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenListingCopilotHandlerMissing_MarksJobTerminalWithoutLifecycle()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.LISTING_COPILOT,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        _registry.GetHandler(AssetProcessingJobType.LISTING_COPILOT).Returns((IAssetProcessingJobHandlerAdapter?)null);

        var callCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }

                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        var terminalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.MarkFailedTerminal(jobId, leaseToken, ErrorCodes.MISSING_HANDLER, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                terminalTcs.TrySetResult(true);
                return Task.FromResult(true);
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await terminalTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await _store.Received(1).MarkFailedTerminal(
            jobId,
            leaseToken,
            ErrorCodes.MISSING_HANDLER,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<AssetProcessingJobType>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenListingCopilotExhaustsRetries_MarksJobTerminalWithoutLifecycle()
    {
        AssetProcessingOptions options = CreateDefaultOptions();
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.LISTING_COPILOT,
            1,
            3,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<AssetProcessingJobOutcome>(_ => throw new InvalidOperationException("copilot boom"));
        _registry.GetHandler(AssetProcessingJobType.LISTING_COPILOT).Returns(adapter);

        var callCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref callCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }

                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        var terminalTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _store.MarkFailedTerminal(jobId, leaseToken, ErrorCodes.PROCESSING_EXCEPTION, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                terminalTcs.TrySetResult(true);
                return Task.FromResult(true);
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await terminalTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await _store.Received(1).MarkFailedTerminal(
            jobId,
            leaseToken,
            ErrorCodes.PROCESSING_EXCEPTION,
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
        await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<Guid>(),
            Arg.Any<AssetProcessingJobType>(),
            Arg.Any<string>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenLeaseLost_CancelsExecutionAndAbandonsJobWithoutTransition()
    {
        var options = new AssetProcessingOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(20),
            BatchSize = 1,
            Concurrency = 1,
            LeaseDuration = TimeSpan.FromMilliseconds(40),
            OperationTimeout = TimeSpan.FromSeconds(5),
            MaxAttempts = 3,
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(5)
        };
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var adapterCancelledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<AssetProcessingJobOutcome>>(async info =>
            {
                CancellationToken ct = info.Arg<CancellationToken>();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 1));
                }
                catch (OperationCanceledException)
                {
                    adapterCancelledTcs.TrySetResult(true);
                    throw;
                }
            });
        _registry.GetHandler(AssetProcessingJobType.ARCHIVE_INSPECTION).Returns(adapter);

        var claimCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref claimCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }
                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        // Renewal returns false (lease lost)
        _store.RenewLease(jobId, leaseToken, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var cancelled = await adapterCancelledTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancelled.Should().BeTrue();
            await _store.DidNotReceive().MarkSucceeded(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingResult?>(), Arg.Any<CancellationToken>());
            await _store.DidNotReceive().MarkFailedTerminal(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await _store.DidNotReceive().MarkFailedRetryable(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingJobType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_WhenLeaseRenewalThrowsException_CancelsExecutionAndAbandonsJobWithoutTransition()
    {
        var options = new AssetProcessingOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(20),
            BatchSize = 1,
            Concurrency = 1,
            LeaseDuration = TimeSpan.FromMilliseconds(40),
            OperationTimeout = TimeSpan.FromSeconds(5),
            MaxAttempts = 3,
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(5)
        };
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var adapterCancelledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<AssetProcessingJobOutcome>>(async info =>
            {
                CancellationToken ct = info.Arg<CancellationToken>();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 1));
                }
                catch (OperationCanceledException)
                {
                    adapterCancelledTcs.TrySetResult(true);
                    throw;
                }
            });
        _registry.GetHandler(AssetProcessingJobType.ARCHIVE_INSPECTION).Returns(adapter);

        var claimCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref claimCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }
                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        // Renewal throws exception
        _store.RenewLease(jobId, leaseToken, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new InvalidOperationException("DB connection dropped"));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var cancelled = await adapterCancelledTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancelled.Should().BeTrue();
            await _store.DidNotReceive().MarkSucceeded(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingResult?>(), Arg.Any<CancellationToken>());
            await _store.DidNotReceive().MarkFailedTerminal(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await _store.DidNotReceive().MarkFailedRetryable(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingJobType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_WhenAdapterIgnoresCancellationAfterLeaseLoss_AbandonsJobWithoutMarkingSucceeded()
    {
        var options = new AssetProcessingOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(20),
            BatchSize = 1,
            Concurrency = 1,
            LeaseDuration = TimeSpan.FromMilliseconds(40),
            OperationTimeout = TimeSpan.FromSeconds(5),
            MaxAttempts = 3,
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(5)
        };
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var adapterExecutedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseAdapterTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<AssetProcessingJobOutcome>>(async _ =>
            {
                // Ignores cancellation and returns Success after test observes lease loss.
                await releaseAdapterTcs.Task;
                adapterExecutedTcs.TrySetResult(true);
                return AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 1));
            });
        _registry.GetHandler(AssetProcessingJobType.ARCHIVE_INSPECTION).Returns(adapter);

        var claimCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref claimCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }
                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        // Renewal returns false (lease lost)
        _store.RenewLease(jobId, leaseToken, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                releaseAdapterTcs.TrySetResult();
                return Task.FromResult(false);
            });

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await adapterExecutedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await _store.DidNotReceive().MarkSucceeded(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingResult?>(), Arg.Any<CancellationToken>());
            await _store.DidNotReceive().MarkFailedTerminal(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await _store.DidNotReceive().MarkFailedRetryable(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingJobType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_WhenAdapterThrowsNonOceAfterLeaseLoss_AbandonsJobWithoutFailingProcessing()
    {
        var options = new AssetProcessingOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(20),
            BatchSize = 1,
            Concurrency = 1,
            LeaseDuration = TimeSpan.FromMilliseconds(40),
            OperationTimeout = TimeSpan.FromSeconds(5),
            MaxAttempts = 3,
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(5)
        };
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var adapterFailedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<AssetProcessingJobOutcome>>(async info =>
            {
                CancellationToken ct = info.Arg<CancellationToken>();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 1));
                }
                catch (OperationCanceledException)
                {
                    adapterFailedTcs.TrySetResult(true);
                    // Throws non-OCE upon cancellation
                    throw new InvalidOperationException("External service failed during cancellation");
                }
            });
        _registry.GetHandler(AssetProcessingJobType.ARCHIVE_INSPECTION).Returns(adapter);

        var claimCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref claimCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }
                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        // Renewal returns false (lease lost)
        _store.RenewLease(jobId, leaseToken, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(false));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            await adapterFailedTcs.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await WaitUntil(() => worker.ActiveJobsCount == 0);
            await _store.DidNotReceive().MarkSucceeded(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingResult?>(), Arg.Any<CancellationToken>());
            await _store.DidNotReceive().MarkFailedTerminal(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await _store.DidNotReceive().MarkFailedRetryable(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingJobType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task Worker_WhenRenewLeaseThrowsUnexpectedOce_CancelsExecutionAndAbandonsJobWithoutTransition()
    {
        var options = new AssetProcessingOptions
        {
            Enabled = true,
            PollInterval = TimeSpan.FromMilliseconds(20),
            BatchSize = 1,
            Concurrency = 1,
            LeaseDuration = TimeSpan.FromMilliseconds(40),
            OperationTimeout = TimeSpan.FromSeconds(5),
            MaxAttempts = 3,
            InitialRetryDelay = TimeSpan.FromSeconds(1),
            MaxRetryDelay = TimeSpan.FromSeconds(5)
        };
        AssetProcessingWorker worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var claimedJob = new ClaimedAssetProcessingJob(
            jobId,
            Guid.NewGuid(),
            Guid.NewGuid(),
            Guid.NewGuid(),
            AssetProcessingJobType.ARCHIVE_INSPECTION,
            1,
            1,
            3,
            "{}",
            null,
            leaseToken,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);

        var adapterCancelledTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        IAssetProcessingJobHandlerAdapter adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<AssetProcessingJobOutcome>>(async info =>
            {
                CancellationToken ct = info.Arg<CancellationToken>();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), ct);
                    return AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 1));
                }
                catch (OperationCanceledException)
                {
                    adapterCancelledTcs.TrySetResult(true);
                    throw;
                }
            });
        _registry.GetHandler(AssetProcessingJobType.ARCHIVE_INSPECTION).Returns(adapter);

        var claimCount = 0;
        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                if (Interlocked.Increment(ref claimCount) == 1)
                {
                    return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([claimedJob]);
                }
                return Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]);
            });

        // Renewal throws unexpected OperationCanceledException with external/unrelated token
        using var externalCts = new CancellationTokenSource();
        _store.RenewLease(jobId, leaseToken, Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns<Task<bool>>(_ => throw new OperationCanceledException("Custom timeout OCE", externalCts.Token));

        await worker.StartAsync(CancellationToken.None);
        try
        {
            var cancelled = await adapterCancelledTcs.Task.WaitAsync(TimeSpan.FromSeconds(10), externalCts.Token);
            cancelled.Should().BeTrue();
            await _store.DidNotReceive().MarkSucceeded(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingResult?>(), Arg.Any<CancellationToken>());
            await _store.DidNotReceive().MarkFailedTerminal(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
            await _store.DidNotReceive().MarkFailedRetryable(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
            await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingJobType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }

    private static async Task<AssetProcessingJobOutcome> TimeoutThenSucceed(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        return AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 1));
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition())
        {
            timeoutCts.Token.ThrowIfCancellationRequested();
            await Task.Yield();
        }
    }
}
