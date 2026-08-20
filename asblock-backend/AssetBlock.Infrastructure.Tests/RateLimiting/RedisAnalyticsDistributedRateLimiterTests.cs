using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using AssetBlock.Infrastructure.RateLimiting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using StackExchange.Redis;

namespace AssetBlock.Infrastructure.Tests.RateLimiting;

public sealed class RedisAnalyticsDistributedRateLimiterTests
{
    private const string SIGNING_SECRET = "test_signing_secret_at_least_32_chars";

    [Fact]
    public async Task Acquire_WhenRedisThrows_ShouldEnterOutageAndReturnUnavailable()
    {
        var time = new FakeTimeProvider();
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns<RedisResult>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        var limiter = CreateLimiter(database, time);

        var first = await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition");
        var second = await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition");

        first.Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);
        second.Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);
        await database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>());
    }

    [Fact]
    public async Task Acquire_WhenCallerCancels_ShouldThrowWithoutEnteringOutage()
    {
        var time = new FakeTimeProvider();
        var database = Substitute.For<IDatabase>();
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns<RedisResult>(_ => throw new OperationCanceledException(cts.Token));

        var limiter = CreateLimiter(database, time);

        var act = () => limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition", cts.Token).AsTask();

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Acquire_WhenDenied_ShouldNotEnterOutage()
    {
        var time = new FakeTimeProvider();
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(RedisResult.Create([0, 30]));

        var limiter = CreateLimiter(database, time);

        var result = await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition");

        result.Status.Should().Be(AnalyticsRateLimitAcquireStatus.DENIED);
        result.RetryAfter.Should().Be(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Acquire_WhenOutageExpiresAndRedisHealthy_ShouldRecoverOnce()
    {
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger();
        var database = Substitute.For<IDatabase>();
        var calls = 0;
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(_ =>
            {
                calls++;
                if (calls == 1)
                {
                    throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
                }

                return RedisResult.Create([1, 45]);
            });

        var limiter = CreateLimiter(database, time, logger);

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        time.Advance(TimeSpan.FromSeconds(6));

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.ACQUIRED);

        logger.InformationCount.Should().Be(1);
        logger.WarningCount.Should().Be(1);
    }

    [Fact]
    public async Task Acquire_WhenOutageExpiresButRedisStillDown_ShouldNotLogRecovered()
    {
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger();
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns<RedisResult>(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        var limiter = CreateLimiter(database, time, logger);

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        time.Advance(TimeSpan.FromSeconds(6));

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        logger.WarningCount.Should().Be(1);
        logger.InformationCount.Should().Be(0);
        await database.Received(2).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>());
    }

    [Fact]
    public async Task Acquire_WhenHalfOpen_ShouldAllowOnlyOneProbeUnderConcurrency()
    {
        var time = new FakeTimeProvider();
        var database = Substitute.For<IDatabase>();
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evaluateCalls = 0;

        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(async _ =>
            {
                var call = Interlocked.Increment(ref evaluateCalls);
                if (call == 1)
                {
                    throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
                }

                await gate.Task;
                return RedisResult.Create([1, 45]);
            });

        var limiter = CreateLimiter(database, time);

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        time.Advance(TimeSpan.FromSeconds(6));

        var probe = limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition").AsTask();
        await Task.Delay(50);

        var others = await Task.WhenAll(
            Enumerable.Range(0, 20).Select(_ => limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition").AsTask()));

        others.Should().OnlyContain(r => r.Status == AnalyticsRateLimitAcquireStatus.UNAVAILABLE);
        evaluateCalls.Should().Be(2);

        gate.SetResult();
        (await probe).Status.Should().Be(AnalyticsRateLimitAcquireStatus.ACQUIRED);
    }

    [Fact]
    public async Task Acquire_WhenProbeSuccessRacesWithStaleFailure_ShouldKeepOpenBackoff()
    {
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger();
        var database = Substitute.For<IDatabase>();
        var staleFailGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var probeGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evaluateCalls = 0;

        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(async _ =>
            {
                var call = Interlocked.Increment(ref evaluateCalls);
                switch (call)
                {
                    case 1:
                        // Stale NORMAL started while healthy; fails after circuit reopened via probe window.
                        await staleFailGate.Task;
                        throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "stale-down");
                    case 2:
                        // Immediate failure that opens the circuit.
                        throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
                    default:
                        // Half-open probe that succeeds after stale failure has EnterOpen'd.
                        await probeGate.Task;
                        return RedisResult.Create([1, 45]);
                }
            });

        var limiter = CreateLimiter(database, time, logger);

        var staleHealthy = limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition").AsTask();
        await Task.Delay(30);

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        time.Advance(TimeSpan.FromSeconds(6));

        var probe = limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition").AsTask();
        await Task.Delay(30);

        staleFailGate.SetResult();
        (await staleHealthy).Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        probeGate.SetResult();
        (await probe).Status.Should().Be(AnalyticsRateLimitAcquireStatus.ACQUIRED);

        // Probe must not clear OPEN deadline after concurrent EnterOpen.
        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        logger.InformationCount.Should().Be(0);
        evaluateCalls.Should().Be(3);
        await database.Received(3).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>());
    }

    [Fact]
    public async Task Acquire_WhenInFlightHealthySuccessCompletesAfterOpen_ShouldNotCloseCircuit()
    {
        var time = new FakeTimeProvider();
        var logger = new CollectingLogger();
        var database = Substitute.For<IDatabase>();
        var staleGate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var evaluateCalls = 0;

        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(async _ =>
            {
                var call = Interlocked.Increment(ref evaluateCalls);
                if (call == 1)
                {
                    await staleGate.Task;
                    return RedisResult.Create([1, 45]);
                }

                throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down");
            });

        var limiter = CreateLimiter(database, time, logger);

        var staleHealthy = limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition").AsTask();
        await Task.Delay(30);

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        staleGate.SetResult();
        (await staleHealthy).Status.Should().Be(AnalyticsRateLimitAcquireStatus.ACQUIRED);

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        logger.InformationCount.Should().Be(0);
        logger.WarningCount.Should().Be(1);
        await database.Received(2).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>());
    }

    [Fact]
    public async Task Acquire_WhenMalformedRedisElements_ShouldEnterBackoff()
    {
        var time = new FakeTimeProvider();
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(RedisResult.Create([(RedisValue)"invalid", (RedisValue)"30"]));

        var limiter = CreateLimiter(database, time);

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        await database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>());
    }

    [Fact]
    public async Task Acquire_WhenMalformedRedisResult_ShouldEnterBackoff()
    {
        var time = new FakeTimeProvider();
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluateAsync(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(RedisResult.Create([1]));

        var limiter = CreateLimiter(database, time);

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        (await limiter.Acquire(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition"))
            .Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);

        await database.Received(1).ScriptEvaluateAsync(
            Arg.Any<string>(),
            Arg.Any<RedisKey[]>(),
            Arg.Any<RedisValue[]>());
    }

    [Fact]
    public async Task Acquire_WhenInvalidPolicy_ShouldFailFast()
    {
        var limiter = CreateLimiter(Substitute.For<IDatabase>(), new FakeTimeProvider());

        var act = () => limiter.Acquire((AnalyticsRateLimitPolicy)999, "partition").AsTask();

        await act.Should().ThrowAsync<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void AcquireBlocking_WhenRedisUnavailable_ShouldReturnUnavailableWithoutAsyncWait()
    {
        var time = new FakeTimeProvider();
        var database = Substitute.For<IDatabase>();
        database.ScriptEvaluate(Arg.Any<string>(), Arg.Any<RedisKey[]>(), Arg.Any<RedisValue[]>())
            .Returns(_ => throw new RedisConnectionException(ConnectionFailureType.UnableToConnect, "down"));

        var limiter = CreateLimiter(database, time);

        var result = limiter.AcquireBlocking(AnalyticsRateLimitPolicy.ANALYTICS_EVENTS, "partition");

        result.Status.Should().Be(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);
    }

    private static RedisAnalyticsDistributedRateLimiter CreateLimiter(
        IDatabase database,
        FakeTimeProvider time,
        ILogger<RedisAnalyticsDistributedRateLimiter>? logger = null)
    {
        var multiplexer = Substitute.For<IConnectionMultiplexer>();
        multiplexer.GetDatabase(Arg.Any<int>(), Arg.Any<object?>()).Returns(database);

        return new RedisAnalyticsDistributedRateLimiter(
            multiplexer,
            Microsoft.Extensions.Options.Options.Create(new AnalyticsRateLimitingOptions { BffSigningSecret = SIGNING_SECRET }),
            time,
            logger ?? NullLogger<RedisAnalyticsDistributedRateLimiter>.Instance);
    }

    private sealed class FakeTimeProvider : TimeProvider
    {
        private DateTimeOffset _utcNow = DateTimeOffset.UtcNow;

        public void Advance(TimeSpan delta) => _utcNow = _utcNow.Add(delta);

        public override DateTimeOffset GetUtcNow() => _utcNow;
    }

    private sealed class CollectingLogger : ILogger<RedisAnalyticsDistributedRateLimiter>
    {
        public int WarningCount { get; private set; }
        public int InformationCount { get; private set; }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (logLevel == LogLevel.Warning)
            {
                WarningCount++;
            }

            if (logLevel == LogLevel.Information)
            {
                InformationCount++;
            }
        }
    }
}
