namespace AssetBlock.Domain.Abstractions.Services;

public enum AnalyticsRateLimitPolicy
{
    ANALYTICS_EVENTS,
    SELLER_ANALYTICS_SALES_EXPORT
}

public enum AnalyticsRateLimitAcquireStatus
{
    ACQUIRED,
    DENIED,
    UNAVAILABLE
}

public sealed record AnalyticsRateLimitAcquireResult(
    AnalyticsRateLimitAcquireStatus Status,
    TimeSpan? RetryAfter = null);

/// <summary>
/// Authoritative fixed-window rate limiter for analytics ingestion and seller CSV export.
/// Redis-backed in Staging/Production; in-memory fallback when Redis is not configured.
/// </summary>
public interface IAnalyticsDistributedRateLimiter
{
    /// <summary>
    /// Blocking acquire for <see cref="System.Threading.RateLimiting.RateLimiter.AttemptAcquireCore"/>.
    /// Must not sync-over-async on Redis; implementations use sync I/O or in-memory logic only.
    /// </summary>
    AnalyticsRateLimitAcquireResult AcquireBlocking(
        AnalyticsRateLimitPolicy policy,
        string partitionMaterial);

    ValueTask<AnalyticsRateLimitAcquireResult> Acquire(
        AnalyticsRateLimitPolicy policy,
        string partitionMaterial,
        CancellationToken cancellationToken = default);
}
