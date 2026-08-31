using System.Collections.Concurrent;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;

namespace AssetBlock.Infrastructure.RateLimiting;

/// <summary>
/// Per-process fixed-window limiter for Development/IntegrationTesting when Redis is not configured.
/// </summary>
internal sealed class InMemoryAnalyticsDistributedRateLimiter(TimeProvider timeProvider) : IAnalyticsDistributedRateLimiter
{
    private const int CLEANUP_INTERVAL = 64;
    private readonly ConcurrentDictionary<string, WindowState> _windows = new();
    private int _operationsSinceCleanup;

    internal int WindowCount => _windows.Count;

    public AnalyticsRateLimitAcquireResult AcquireBlocking(
        AnalyticsRateLimitPolicy policy,
        string partitionMaterial) =>
        AcquireCore(policy, partitionMaterial);

    public ValueTask<AnalyticsRateLimitAcquireResult> Acquire(
        AnalyticsRateLimitPolicy policy,
        string partitionMaterial,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(AcquireCore(policy, partitionMaterial));
    }

    private AnalyticsRateLimitAcquireResult AcquireCore(
        AnalyticsRateLimitPolicy policy,
        string partitionMaterial)
    {
        (var limit, var windowSeconds) = ResolvePolicy(policy);
        DateTimeOffset now = timeProvider.GetUtcNow();
        DateTimeOffset windowStart = GetWindowStart(now, windowSeconds);
        TimeSpan retryAfter = windowStart.AddSeconds(windowSeconds) - now;
        if (retryAfter <= TimeSpan.Zero)
        {
            retryAfter = TimeSpan.FromSeconds(1);
        }

        MaybeCleanupExpiredWindows(now);

        var key = $"{policy}:{partitionMaterial}:{windowStart.ToUnixTimeSeconds()}";
        WindowState state = _windows.GetOrAdd(key, _ => new WindowState(windowStart, windowSeconds));
        var count = Interlocked.Increment(ref state.Count);

        if (count > limit)
        {
            return new AnalyticsRateLimitAcquireResult(AnalyticsRateLimitAcquireStatus.DENIED, retryAfter);
        }

        return new AnalyticsRateLimitAcquireResult(AnalyticsRateLimitAcquireStatus.ACQUIRED, retryAfter);
    }

    private void MaybeCleanupExpiredWindows(DateTimeOffset now)
    {
        var tick = Interlocked.Increment(ref _operationsSinceCleanup);
        if (tick % CLEANUP_INTERVAL != 0)
        {
            return;
        }

        foreach (KeyValuePair<string, WindowState> entry in _windows)
        {
            if (entry.Value.IsExpired(now))
            {
                _windows.TryRemove(entry.Key, out _);
            }
        }
    }

    private static (int Limit, int WindowSeconds) ResolvePolicy(AnalyticsRateLimitPolicy policy) =>
        policy switch
        {
            AnalyticsRateLimitPolicy.ANALYTICS_EVENTS => (
                RateLimitingConstants.Windows.ANALYTICS_EVENTS_LIMIT,
                RateLimitingConstants.Windows.ANALYTICS_EVENTS_PERIOD_SECONDS),
            AnalyticsRateLimitPolicy.SELLER_ANALYTICS_SALES_EXPORT => (
                RateLimitingConstants.Windows.SELLER_ANALYTICS_SALES_EXPORT_LIMIT,
                RateLimitingConstants.Windows.SELLER_ANALYTICS_SALES_EXPORT_PERIOD_SECONDS),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };

    private static DateTimeOffset GetWindowStart(DateTimeOffset now, int windowSeconds)
    {
        var unix = now.ToUnixTimeSeconds();
        var bucket = unix / windowSeconds * windowSeconds;
        return DateTimeOffset.FromUnixTimeSeconds(bucket);
    }

    private sealed class WindowState(DateTimeOffset windowStart, int windowSeconds)
    {
        public int Count;
        private readonly DateTimeOffset _expiresAt = windowStart.AddSeconds(windowSeconds * 2);

        public bool IsExpired(DateTimeOffset now) => now >= _expiresAt;
    }
}
