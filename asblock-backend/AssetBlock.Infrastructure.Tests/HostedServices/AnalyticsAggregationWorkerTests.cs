using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Analytics;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.HostedServices;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace AssetBlock.Infrastructure.Tests.HostedServices;

public sealed class AnalyticsAggregationWorkerTests
{
    private static readonly DateTimeOffset _fixedNow = new(2026, 7, 29, 12, 0, 0, TimeSpan.Zero);

    private static (AnalyticsAggregationWorker Worker, ServiceProvider Provider, IAnalyticsEventStore Store) BuildWorker(
        AnalyticsAggregationOptions? options = null,
        DateOnly? lastRetentionDayUtc = null)
    {
        IAnalyticsEventStore store = Substitute.For<IAnalyticsEventStore>();
        var services = new ServiceCollection();
        services.AddScoped(_ => store);
        ServiceProvider provider = services.BuildServiceProvider();

        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);

        TimeProvider timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(_fixedNow);

        var worker = new AnalyticsAggregationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(options ?? new AnalyticsAggregationOptions()),
            timeProvider,
            environment,
            NullLogger<AnalyticsAggregationWorker>.Instance);

        if (lastRetentionDayUtc.HasValue)
        {
            typeof(AnalyticsAggregationWorker)
                .GetField("_lastRetentionDayUtc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
                .SetValue(worker, lastRetentionDayUtc.Value);
        }

        return (worker, provider, store);
    }

    [Fact]
    public async Task RunIteration_WhenLockAcquired_ShouldRecomputeCurrentAndPreviousUtcDays()
    {
        IAnalyticsEventStore store = Substitute.For<IAnalyticsEventStore>();
        var services = new ServiceCollection();
        services.AddScoped(_ => store);
        ServiceProvider provider = services.BuildServiceProvider();

        store.TryAcquireAndRecomputeDaily(
                Arg.Any<DateOnly>(),
                Arg.Any<DateOnly>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new AnalyticsDailyRecomputeResult(AnalyticsDailyRecomputeOutcome.COMPLETED, 1, 2, 3, 4));
        store.TryAcquireAndDeleteExpiredEvents(
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new AnalyticsEventRetentionResult(0, false));

        IHostEnvironment environment = Substitute.For<IHostEnvironment>();
        environment.EnvironmentName.Returns(Environments.Development);
        TimeProvider timeProvider = Substitute.For<TimeProvider>();
        timeProvider.GetUtcNow().Returns(_fixedNow);

        var worker = new AnalyticsAggregationWorker(
            provider.GetRequiredService<IServiceScopeFactory>(),
            Microsoft.Extensions.Options.Options.Create(new AnalyticsAggregationOptions()),
            timeProvider,
            environment,
            NullLogger<AnalyticsAggregationWorker>.Instance);

        await using (provider)
        {
            await worker.RunIteration(CancellationToken.None);
        }

        await store.Received(1).TryAcquireAndRecomputeDaily(
            new DateOnly(2026, 7, 29),
            new DateOnly(2026, 7, 28),
            _fixedNow,
            120,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIteration_WhenRetentionAlreadyRanToday_ShouldSkipRetention()
    {
        (AnalyticsAggregationWorker worker, ServiceProvider provider, IAnalyticsEventStore store) = BuildWorker(lastRetentionDayUtc: new DateOnly(2026, 7, 29));
        store.TryAcquireAndRecomputeDaily(
                Arg.Any<DateOnly>(),
                Arg.Any<DateOnly>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new AnalyticsDailyRecomputeResult(AnalyticsDailyRecomputeOutcome.SKIPPED, 0, 0, 0, 0));

        await using (provider)
        {
            await worker.RunIteration(CancellationToken.None);
        }

        await store.DidNotReceive().TryAcquireAndDeleteExpiredEvents(
            Arg.Any<DateTimeOffset>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIteration_WhenRetentionNotYetRunToday_ShouldDeleteBeforeRetentionCutoff()
    {
        (AnalyticsAggregationWorker worker, ServiceProvider provider, IAnalyticsEventStore store) = BuildWorker();
        store.TryAcquireAndRecomputeDaily(
                Arg.Any<DateOnly>(),
                Arg.Any<DateOnly>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new AnalyticsDailyRecomputeResult(AnalyticsDailyRecomputeOutcome.COMPLETED, 0, 0, 0, 0));
        store.TryAcquireAndDeleteExpiredEvents(
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new AnalyticsEventRetentionResult(10, false));

        await using (provider)
        {
            await worker.RunIteration(CancellationToken.None);
        }

        DateTimeOffset expectedCutoff = _fixedNow - TimeSpan.FromDays(AnalyticsAggregationConstants.RAW_EVENT_RETENTION_DAYS);
        await store.Received(1).TryAcquireAndDeleteExpiredEvents(
            expectedCutoff,
            10_000,
            50,
            120,
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIteration_WhenRetentionHasBacklog_ShouldNotMarkDayComplete()
    {
        (AnalyticsAggregationWorker worker, ServiceProvider provider, IAnalyticsEventStore store) = BuildWorker();
        store.TryAcquireAndRecomputeDaily(
                Arg.Any<DateOnly>(),
                Arg.Any<DateOnly>(),
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new AnalyticsDailyRecomputeResult(AnalyticsDailyRecomputeOutcome.COMPLETED, 0, 0, 0, 0));
        store.TryAcquireAndDeleteExpiredEvents(
                Arg.Any<DateTimeOffset>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<int>(),
                Arg.Any<CancellationToken>())
            .Returns(new AnalyticsEventRetentionResult(10_000, true));

        await using (provider)
        {
            await worker.RunIteration(CancellationToken.None);
            await worker.RunIteration(CancellationToken.None);
        }

        await store.Received(2).TryAcquireAndDeleteExpiredEvents(
            Arg.Any<DateTimeOffset>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task RunIteration_WhenDisabled_ShouldNotCallStore()
    {
        (AnalyticsAggregationWorker worker, ServiceProvider provider, IAnalyticsEventStore store) = BuildWorker(new AnalyticsAggregationOptions { Enabled = false });

        await using (provider)
        {
            await worker.RunIteration(CancellationToken.None);
        }

        await store.DidNotReceive().TryAcquireAndRecomputeDaily(
            Arg.Any<DateOnly>(),
            Arg.Any<DateOnly>(),
            Arg.Any<DateTimeOffset>(),
            Arg.Any<int>(),
            Arg.Any<CancellationToken>());
    }
}
