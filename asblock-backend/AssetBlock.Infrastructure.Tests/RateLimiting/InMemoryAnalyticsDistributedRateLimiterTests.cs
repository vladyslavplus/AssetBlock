using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Infrastructure.RateLimiting;

namespace AssetBlock.Infrastructure.Tests.RateLimiting;

public sealed class InMemoryAnalyticsDistributedRateLimiterTests
{
    [Fact]
    public async Task Acquire_WhenWithinLimit_ShouldAcquire()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var limiter = new InMemoryAnalyticsDistributedRateLimiter(time);

        AnalyticsRateLimitAcquireResult result = await limiter.Acquire(
            AnalyticsRateLimitPolicy.ANALYTICS_EVENTS,
            "partition-a");

        result.Status.Should().Be(AnalyticsRateLimitAcquireStatus.ACQUIRED);
    }

    [Fact]
    public async Task Acquire_WhenExceedingLimit_ShouldDenyWithRetryAfter()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var limiter = new InMemoryAnalyticsDistributedRateLimiter(time);
        var limit = RateLimitingConstants.Windows.ANALYTICS_EVENTS_LIMIT;

        for (var i = 0; i < limit; i++)
        {
            (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition-a"))
                .Status.Should().Be(AnalyticsRateLimitAcquireStatus.ACQUIRED);
        }

        AnalyticsRateLimitAcquireResult denied = await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition-a");
        denied.Status.Should().Be(AnalyticsRateLimitAcquireStatus.DENIED);
        denied.RetryAfter.Should().NotBeNull();
        denied.RetryAfter!.Value.Should().BeGreaterThan(TimeSpan.Zero);
    }

    [Fact]
    public async Task Acquire_WhenPartitionsDiffer_ShouldBeIndependent()
    {
        var time = new FakeTimeProvider(new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero));
        var limiter = new InMemoryAnalyticsDistributedRateLimiter(time);
        var limit = RateLimitingConstants.Windows.ANALYTICS_EVENTS_LIMIT;

        for (var i = 0; i < limit; i++)
        {
            await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition-a");
        }

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition-a"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.DENIED);
        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition-b"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.ACQUIRED);
    }

    [Fact]
    public async Task Acquire_WhenWindowRollover_ShouldResetCount()
    {
        var start = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var limiter = new InMemoryAnalyticsDistributedRateLimiter(time);
        var limit = RateLimitingConstants.Windows.ANALYTICS_EVENTS_LIMIT;

        for (var i = 0; i < limit; i++)
        {
            await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition-a");
        }

        time.Advance(TimeSpan.FromSeconds(RateLimitingConstants.Windows.ANALYTICS_EVENTS_PERIOD_SECONDS));

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition-a"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.ACQUIRED);
    }

    [Fact]
    public async Task Acquire_WhenExpiredWindowsAccumulate_ShouldReduceWindowCountOnCleanup()
    {
        var start = new DateTimeOffset(2024, 1, 1, 12, 0, 0, TimeSpan.Zero);
        var time = new FakeTimeProvider(start);
        var limiter = new InMemoryAnalyticsDistributedRateLimiter(time);
        var period = RateLimitingConstants.Windows.ANALYTICS_EVENTS_PERIOD_SECONDS;

        for (var window = 0; window < 80; window++)
        {
            await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, $"partition-{window}");
            time.Advance(TimeSpan.FromSeconds(period * 3));
        }

        // Cleanup runs every 64 ops; expired entries from earlier windows must be gone.
        limiter.WindowCount.Should().BeLessThan(80);
        limiter.WindowCount.Should().BeLessThanOrEqualTo(20);

        time.Advance(TimeSpan.FromSeconds(period * 10));
        for (var i = 0; i < 64; i++)
        {
            await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "force-cleanup");
        }

        limiter.WindowCount.Should().BeLessThanOrEqualTo(2);
    }

    [Fact]
    public void AcquireBlocking_WhenInvalidPolicy_ShouldThrow()
    {
        var limiter = new InMemoryAnalyticsDistributedRateLimiter(TimeProvider.System);

        Func<AnalyticsRateLimitAcquireResult> act = () => limiter.AcquireBlocking((AnalyticsRateLimitPolicy)999, "partition");

        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _utcNow = start;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }
}
