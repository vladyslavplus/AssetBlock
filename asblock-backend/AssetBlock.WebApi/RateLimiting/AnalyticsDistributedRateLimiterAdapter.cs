using AssetBlock.Domain.Abstractions.Services;
using System.Threading.RateLimiting;

namespace AssetBlock.WebApi.RateLimiting;

internal static class AnalyticsRateLimitMetadataNames
{
    public static readonly MetadataName<bool> Unavailable = MetadataName.Create<bool>("analytics-rate-limit-unavailable");
    public static readonly MetadataName<TimeSpan> RetryAfter = MetadataName.Create<TimeSpan>("analytics-rate-limit-retry-after");
}

internal sealed class AnalyticsDistributedRateLimiterAdapter(
    IAnalyticsDistributedRateLimiter limiter,
    AnalyticsRateLimitPolicy policy,
    string partitionMaterial,
    TimeProvider timeProvider) : RateLimiter
{
    private long _lastAcquireTimestamp = timeProvider.GetTimestamp();
    private int _disposed;

    public override TimeSpan? IdleDuration
    {
        get
        {
            TimeSpan elapsed = timeProvider.GetElapsedTime(Interlocked.Read(ref _lastAcquireTimestamp));
            return elapsed;
        }
    }

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(permitCount, 1);

        Interlocked.Exchange(ref _lastAcquireTimestamp, timeProvider.GetTimestamp());
        AnalyticsRateLimitAcquireResult result = limiter.AcquireBlocking(policy, partitionMaterial);
        return MapResult(result);
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(int permitCount, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(permitCount, 1);

        return AcquireInternal(cancellationToken);
    }

    private async ValueTask<RateLimitLease> AcquireInternal(CancellationToken cancellationToken)
    {
        Interlocked.Exchange(ref _lastAcquireTimestamp, timeProvider.GetTimestamp());
        AnalyticsRateLimitAcquireResult result = await limiter.Acquire(policy, partitionMaterial, cancellationToken);
        return MapResult(result);
    }

    private static RateLimitLease MapResult(AnalyticsRateLimitAcquireResult result) =>
        result.Status switch
        {
            AnalyticsRateLimitAcquireStatus.ACQUIRED => AcquiredAnalyticsRateLimitLease.Instance,
            AnalyticsRateLimitAcquireStatus.DENIED => new AnalyticsRateLimitLease(
                acquired: false,
                unavailable: false,
                result.RetryAfter ?? TimeSpan.FromSeconds(1)),
            AnalyticsRateLimitAcquireStatus.UNAVAILABLE => new AnalyticsRateLimitLease(
                acquired: false,
                unavailable: true,
                retryAfter: null),
            _ => throw new InvalidOperationException($"Unknown rate limit status: {result.Status}")
        };

    protected override void Dispose(bool disposing)
    {
        Interlocked.Exchange(ref _disposed, 1);
        base.Dispose(disposing);
    }

    private sealed class AcquiredAnalyticsRateLimitLease : RateLimitLease
    {
        public static readonly AcquiredAnalyticsRateLimitLease Instance = new();

        public override bool IsAcquired => true;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }

    private sealed class AnalyticsRateLimitLease(bool acquired, bool unavailable, TimeSpan? retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => acquired;

        public override IEnumerable<string> MetadataNames
        {
            get
            {
                if (unavailable)
                {
                    yield return AnalyticsRateLimitMetadataNames.Unavailable.Name;
                }

                if (retryAfter is not null)
                {
                    yield return AnalyticsRateLimitMetadataNames.RetryAfter.Name;
                }
            }
        }

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == AnalyticsRateLimitMetadataNames.Unavailable.Name)
            {
                metadata = unavailable;
                return true;
            }

            if (metadataName == AnalyticsRateLimitMetadataNames.RetryAfter.Name && retryAfter is not null)
            {
                metadata = retryAfter.Value;
                return true;
            }

            metadata = null;
            return false;
        }
    }
}
