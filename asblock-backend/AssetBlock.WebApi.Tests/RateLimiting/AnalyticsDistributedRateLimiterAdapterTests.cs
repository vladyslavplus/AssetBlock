using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.WebApi.RateLimiting;
using AwesomeAssertions;
using NSubstitute;

namespace AssetBlock.WebApi.Tests.RateLimiting;

public sealed class AnalyticsDistributedRateLimiterAdapterTests
{
    [Fact]
    public void IdleDuration_WhenInactive_ShouldGrow()
    {
        var limiter = Substitute.For<IAnalyticsDistributedRateLimiter>();
        limiter.AcquireBlocking(Arg.Any<AnalyticsRateLimitPolicy>(), Arg.Any<string>())
            .Returns(new AnalyticsRateLimitAcquireResult(AnalyticsRateLimitAcquireStatus.ACQUIRED));

        var time = new FakeTimeProvider();
        using var adapter = new AnalyticsDistributedRateLimiterAdapter(
            limiter,
            AnalyticsRateLimitPolicy.ANALYTICS_EVENTS,
            "partition",
            time);

        time.Advance(TimeSpan.FromMilliseconds(25));
        adapter.IdleDuration.Should().NotBeNull();
        adapter.IdleDuration!.Value.Should().BeGreaterThanOrEqualTo(TimeSpan.FromMilliseconds(25));
    }

    [Fact]
    public void AttemptAcquire_WhenSuccessful_ShouldResetIdle()
    {
        var limiter = Substitute.For<IAnalyticsDistributedRateLimiter>();
        limiter.AcquireBlocking(Arg.Any<AnalyticsRateLimitPolicy>(), Arg.Any<string>())
            .Returns(new AnalyticsRateLimitAcquireResult(AnalyticsRateLimitAcquireStatus.ACQUIRED));

        var time = new FakeTimeProvider();
        using var adapter = new AnalyticsDistributedRateLimiterAdapter(
            limiter,
            AnalyticsRateLimitPolicy.ANALYTICS_EVENTS,
            "partition",
            time);

        time.Advance(TimeSpan.FromMilliseconds(40));
        _ = adapter.AttemptAcquire(1);
        adapter.IdleDuration!.Value.Should().BeLessThan(TimeSpan.FromMilliseconds(5));
    }

    [Fact]
    public void Dispose_WhenCalledTwice_ShouldBeIdempotent()
    {
        var limiter = Substitute.For<IAnalyticsDistributedRateLimiter>();
        var adapter = new AnalyticsDistributedRateLimiterAdapter(
            limiter,
            AnalyticsRateLimitPolicy.ANALYTICS_EVENTS,
            "partition",
            TimeProvider.System);

        adapter.Dispose();
        var act = adapter.Dispose;

        act.Should().NotThrow();
    }

    [Fact]
    public void AttemptAcquire_WhenPermitCountNotOne_ShouldThrow()
    {
        var limiter = Substitute.For<IAnalyticsDistributedRateLimiter>();
        using var adapter = new AnalyticsDistributedRateLimiterAdapter(
            limiter,
            AnalyticsRateLimitPolicy.ANALYTICS_EVENTS,
            "partition",
            TimeProvider.System);

        var act = () => adapter.AttemptAcquire(2);

        act.Should().Throw<ArgumentOutOfRangeException>();
        limiter.DidNotReceive().AcquireBlocking(Arg.Any<AnalyticsRateLimitPolicy>(), Arg.Any<string>());
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private long _timestamp;

        public void Advance(TimeSpan delta) =>
            _timestamp += (long)(delta.TotalSeconds * TimestampFrequency);

        public override long TimestampFrequency => 10_000_000;

        public override long GetTimestamp() => _timestamp;

        public override DateTimeOffset GetUtcNow() =>
            DateTimeOffset.UnixEpoch.AddTicks(_timestamp);
    }
}
