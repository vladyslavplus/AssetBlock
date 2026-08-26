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

    private AssetProcessingWorker CreateWorker(AssetProcessingOptions options)
    {
        var serviceProvider = Substitute.For<IServiceProvider>();
        var scope = Substitute.For<IServiceScope>();

        serviceProvider.GetService(typeof(IAssetProcessingJobStore)).Returns(_store);
        serviceProvider.GetService(typeof(IAssetProcessingLifecycleStore)).Returns(_lifecycleStore);
        scope.ServiceProvider.Returns(serviceProvider);
        _scopeFactory.CreateScope().Returns(scope);

        return new AssetProcessingWorker(
            _scopeFactory,
            _registry,
            _publisher,
            Microsoft.Extensions.Options.Options.Create(options),
            _timeProvider,
            NullLogger<AssetProcessingWorker>.Instance);
    }

    [Theory]
    [InlineData(1, 10)]   // 10s * 2^0 = 10s
    [InlineData(2, 20)]   // 10s * 2^1 = 20s
    [InlineData(3, 40)]   // 10s * 2^2 = 40s
    [InlineData(4, 80)]   // 10s * 2^3 = 80s
    [InlineData(10, 300)] // Capped at MaxRetryDelay (300s)
    public void CalculateRetryDelay_ExponentialBackoff_CalculatesCorrectDelay(int attempt, int expectedSeconds)
    {
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

        var delay = worker.CalculateRetryDelay(attempt, null);

        delay.Should().Be(TimeSpan.FromSeconds(expectedSeconds));
    }

    [Fact]
    public void CalculateRetryDelay_WhenHandlerProvidesLargerRetryAfter_UsesHandlerValue()
    {
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

        var handlerRetryAfter = TimeSpan.FromMinutes(2); // 120s > 10s exponential
        var delay = worker.CalculateRetryDelay(1, handlerRetryAfter);

        delay.Should().Be(TimeSpan.FromMinutes(2));
    }

    [Fact]
    public void CalculateRetryDelay_WhenHandlerProvidesValueExceedingMax_CapsAtMaxRetryDelay()
    {
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

        var handlerRetryAfter = TimeSpan.FromHours(1); // 1h > 5min max
        var delay = worker.CalculateRetryDelay(1, handlerRetryAfter);

        delay.Should().Be(options.MaxRetryDelay);
    }

    [Fact]
    public async Task Worker_WhenRecoveringLeases_AlsoRecoversExhaustedSecurityJobs()
    {
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

        _store.ClaimPendingBatch(Arg.Any<int>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<ClaimedAssetProcessingJob>>([]));
        _store.RecoverExpiredLeases(Arg.Any<CancellationToken>()).Returns(0);
        _lifecycleStore.RecoverExpiredExhaustedSecurityJobs(Arg.Any<CancellationToken>()).Returns(0);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));

        await worker.StartAsync(cts.Token);
        await Task.Delay(80);
        await worker.StopAsync(CancellationToken.None);

        await _store.Received().RecoverExpiredLeases(Arg.Any<CancellationToken>());
        await _lifecycleStore.Received().RecoverExpiredExhaustedSecurityJobs(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenDisabled_DoesNotClaimJobs()
    {
        var options = CreateDefaultOptions(enabled: false);
        var worker = CreateWorker(options);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));

        await worker.StartAsync(cts.Token);
        await Task.Delay(50, cts.Token);
        await worker.StopAsync(CancellationToken.None);

        await _store.DidNotReceive().ClaimPendingBatch(
            Arg.Any<int>(),
            Arg.Any<TimeSpan>(),
            Arg.Any<string>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Worker_WhenHandlerThrowsInvalidResultException_MarksTerminalWithoutRetry()
    {
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

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

        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
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

        _lifecycleStore.TransitionProcessingFailed(jobId, leaseToken, claimedJob.AssetId, claimedJob.AssetVersionId, AssetProcessingJobType.ARCHIVE_INSPECTION, "INVALID_JOB_RESULT", Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await worker.StartAsync(cts.Token);
        await Task.Delay(100, cts.Token);
        await worker.StopAsync(CancellationToken.None);

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
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

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

        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
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
            .Returns<bool>(_ => throw new InvalidOperationException("PostgreSQL connection severed"));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await worker.StartAsync(cts.Token);
        await Task.Delay(100, cts.Token);
        await worker.StopAsync(CancellationToken.None);

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
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

        var jobId = Guid.NewGuid();
        var leaseToken = Guid.NewGuid();
        var ownerUserId = Guid.NewGuid();
        var updatedAt = DateTimeOffset.UtcNow.AddSeconds(-1);

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

        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
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
        _publisher.PublishJobUpdated(ownerUserId, Arg.Do<AssetProcessingUpdateMessage>(publishedMessages.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await worker.StartAsync(cts.Token);
        await Task.Delay(100, cts.Token);
        await worker.StopAsync(CancellationToken.None);

        publishedMessages.Should().HaveCount(2);
        publishedMessages[0].Status.Should().Be(AssetProcessingJobStatus.RUNNING);
        publishedMessages[0].UpdatedAt.Should().Be(updatedAt);
        publishedMessages[1].Status.Should().Be(AssetProcessingJobStatus.SUCCEEDED);
    }

    [Fact]
    public async Task Worker_WhenListingCopilotCommitsAtomically_ShouldPublishFinalStateOnceWithoutMarkSucceeded()
    {
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

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

        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
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

        var publishedMessages = new List<AssetProcessingUpdateMessage>();
        _publisher.PublishJobUpdated(ownerUserId, Arg.Do<AssetProcessingUpdateMessage>(publishedMessages.Add), Arg.Any<CancellationToken>())
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
        await worker.StartAsync(cts.Token);
        await Task.Delay(100, cts.Token);
        await worker.StopAsync(CancellationToken.None);

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
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

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

        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
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

        _store.MarkFailedRetryable(
                jobId,
                leaseToken,
                ErrorCodes.PROCESSING_EXCEPTION,
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await worker.StartAsync(cts.Token);
        await Task.Delay(120, cts.Token);
        await worker.StopAsync(CancellationToken.None);

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
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

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

        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
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

        _store.MarkFailedRetryable(
                jobId,
                leaseToken,
                ErrorCodes.SCANNER_UNAVAILABLE,
                Arg.Any<string>(),
                Arg.Any<TimeSpan>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await worker.StartAsync(cts.Token);
        await Task.Delay(120, cts.Token);
        await worker.StopAsync(CancellationToken.None);

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
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

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

        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
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

        _lifecycleStore.TransitionProcessingFailed(
                jobId,
                leaseToken,
                claimedJob.AssetId,
                claimedJob.AssetVersionId,
                AssetProcessingJobType.MALWARE_SCAN,
                ErrorCodes.SCANNER_UNAVAILABLE,
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await worker.StartAsync(cts.Token);
        await Task.Delay(120, cts.Token);
        await worker.StopAsync(CancellationToken.None);

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
        var worker = CreateWorker(options);

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

        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        if (mode == "timeout")
        {
            adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
                .Returns(ci => TimeoutThenSucceed(ci.Arg<CancellationToken>()));
        }
        else if (mode == "payload")
        {
            adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
                .Returns<AssetProcessingJobOutcome>(_ => throw new AssetProcessingSerializerException("bad payload"));
        }
        else
        {
            adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
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

        _lifecycleStore.TransitionProcessingFailed(
                jobId,
                leaseToken,
                claimedJob.AssetId,
                claimedJob.AssetVersionId,
                AssetProcessingJobType.MALWARE_SCAN,
                Arg.Any<string>(),
                Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(400));
        await worker.StartAsync(cts.Token);
        await Task.Delay(200, cts.Token);
        await worker.StopAsync(CancellationToken.None);

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
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

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

        _store.MarkFailedTerminal(jobId, leaseToken, ErrorCodes.MISSING_HANDLER, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await worker.StartAsync(cts.Token);
        await Task.Delay(120, cts.Token);
        await worker.StopAsync(CancellationToken.None);

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
        var options = CreateDefaultOptions();
        var worker = CreateWorker(options);

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

        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
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

        _store.MarkFailedTerminal(jobId, leaseToken, ErrorCodes.PROCESSING_EXCEPTION, Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(true));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));
        await worker.StartAsync(cts.Token);
        await Task.Delay(120, cts.Token);
        await worker.StopAsync(CancellationToken.None);

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
        var worker = CreateWorker(options);

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

        var adapterCancelled = false;
        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<AssetProcessingJobOutcome>>(async info =>
            {
                var ct = info.Arg<CancellationToken>();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 1));
                }
                catch (OperationCanceledException)
                {
                    adapterCancelled = true;
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

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await worker.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);
        await worker.StopAsync(CancellationToken.None);

        adapterCancelled.Should().BeTrue();
        await _store.DidNotReceive().MarkSucceeded(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingResult?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedTerminal(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedRetryable(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingJobType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        var worker = CreateWorker(options);

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

        var adapterCancelled = false;
        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<AssetProcessingJobOutcome>>(async info =>
            {
                var ct = info.Arg<CancellationToken>();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 1));
                }
                catch (OperationCanceledException)
                {
                    adapterCancelled = true;
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

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await worker.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);
        await worker.StopAsync(CancellationToken.None);

        adapterCancelled.Should().BeTrue();
        await _store.DidNotReceive().MarkSucceeded(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingResult?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedTerminal(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedRetryable(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingJobType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        var worker = CreateWorker(options);

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

        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<AssetProcessingJobOutcome>>(async _ =>
            {
                // Ignores cancellation and returns Success after delay
                await Task.Delay(80);
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
            .Returns(Task.FromResult(false));

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await worker.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);
        await worker.StopAsync(CancellationToken.None);

        await _store.DidNotReceive().MarkSucceeded(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingResult?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedTerminal(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedRetryable(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingJobType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        var worker = CreateWorker(options);

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

        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<AssetProcessingJobOutcome>>(async info =>
            {
                var ct = info.Arg<CancellationToken>();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 1));
                }
                catch (OperationCanceledException)
                {
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

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await worker.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);
        await worker.StopAsync(CancellationToken.None);

        await _store.DidNotReceive().MarkSucceeded(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingResult?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedTerminal(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedRetryable(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingJobType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
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
        var worker = CreateWorker(options);

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

        var adapterCancelled = false;
        var adapter = Substitute.For<IAssetProcessingJobHandlerAdapter>();
        adapter.Execute(Arg.Any<IServiceProvider>(), Arg.Any<ClaimedAssetProcessingJob>(), Arg.Any<CancellationToken>())
            .Returns<Task<AssetProcessingJobOutcome>>(async info =>
            {
                var ct = info.Arg<CancellationToken>();
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(5), ct);
                    return AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 1));
                }
                catch (OperationCanceledException)
                {
                    adapterCancelled = true;
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

        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await worker.StartAsync(cts.Token);
        await Task.Delay(150, cts.Token);
        await worker.StopAsync(CancellationToken.None);

        adapterCancelled.Should().BeTrue();
        await _store.DidNotReceive().MarkSucceeded(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingResult?>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedTerminal(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _store.DidNotReceive().MarkFailedRetryable(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>());
        await _lifecycleStore.DidNotReceive().TransitionProcessingFailed(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<AssetProcessingJobType>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    private static async Task<AssetProcessingJobOutcome> TimeoutThenSucceed(CancellationToken cancellationToken)
    {
        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken);
        return AssetProcessingJobOutcome.Succeeded(new ArchiveInspectionResult(1, 1));
    }
}
