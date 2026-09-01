using System.Threading.RateLimiting;

namespace AssetBlock.WebApi.RateLimiting;

/// <summary>Fails closed when an IP-partitioned auth endpoint has no trusted client address.</summary>
internal sealed class MissingClientIpRateLimiter : RateLimiter
{
    public override TimeSpan? IdleDuration => null;

    public override RateLimiterStatistics? GetStatistics() => null;

    protected override RateLimitLease AttemptAcquireCore(int permitCount)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(permitCount, 1);
        return RejectedLease.Instance;
    }

    protected override ValueTask<RateLimitLease> AcquireAsyncCore(
        int permitCount,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNotEqual(permitCount, 1);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<RateLimitLease>(RejectedLease.Instance);
    }

    private sealed class RejectedLease : RateLimitLease
    {
        public static readonly RejectedLease Instance = new();

        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            metadata = null;
            return false;
        }
    }
}
