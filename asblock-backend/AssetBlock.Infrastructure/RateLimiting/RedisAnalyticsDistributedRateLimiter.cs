using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Primitives.AppSettingsOptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace AssetBlock.Infrastructure.RateLimiting;

internal sealed class RedisAnalyticsDistributedRateLimiter(
    IConnectionMultiplexer multiplexer,
    IOptions<AnalyticsRateLimitingOptions> options,
    TimeProvider timeProvider,
    ILogger<RedisAnalyticsDistributedRateLimiter> logger) : IAnalyticsDistributedRateLimiter
{
    private const string FIXED_WINDOW_SCRIPT = """
        local key = KEYS[1]
        local limit = tonumber(ARGV[1])
        local window = tonumber(ARGV[2])
        local time = redis.call('TIME')
        local now_sec = tonumber(time[1])
        local bucket = math.floor(now_sec / window)
        local bucket_key = key .. ':' .. tostring(bucket)
        local current = tonumber(redis.call('GET', bucket_key) or '0')
        local retry_after = window - (now_sec % window)
        if current >= limit then
            return {0, retry_after}
        end
        local new_count = redis.call('INCR', bucket_key)
        if new_count == 1 then
            redis.call('EXPIRE', bucket_key, window + 1)
        end
        return {1, retry_after}
        """;

    private const int STATE_HEALTHY = 0;
    private const int STATE_OPEN = 1;
    private const int STATE_HALF_OPEN = 2;

    private static readonly TimeSpan _outageBackoff = TimeSpan.FromSeconds(5);

    private readonly IDatabase _database = multiplexer.GetDatabase();
    private readonly string _keySecret = options.Value.BffSigningSecret;
    private readonly Lock _circuitLock = new();
    private int _circuitState;
    private long _openUntilTimestamp;
    private int _outageLogged;

    private enum CallPermit
    {
        DENIED,
        NORMAL,
        PROBE
    }

    public AnalyticsRateLimitAcquireResult AcquireBlocking(
        AnalyticsRateLimitPolicy policy,
        string partitionMaterial)
    {
        (int Limit, int WindowSeconds, string Domain) resolved = ResolvePolicy(policy);
        CallPermit permit = TryBeginCall();
        if (permit == CallPermit.DENIED)
        {
            return new AnalyticsRateLimitAcquireResult(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);
        }

        try
        {
            AnalyticsRateLimitAcquireResult result = EvaluateScript(resolved, policy, partitionMaterial);
            CompleteSuccess(permit);
            return result;
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            EnterOpen(ex, policy);
            return new AnalyticsRateLimitAcquireResult(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);
        }
    }

    public async ValueTask<AnalyticsRateLimitAcquireResult> Acquire(
        AnalyticsRateLimitPolicy policy,
        string partitionMaterial,
        CancellationToken cancellationToken = default)
    {
        (int Limit, int WindowSeconds, string Domain) resolved = ResolvePolicy(policy);
        CallPermit permit = TryBeginCall();
        if (permit == CallPermit.DENIED)
        {
            return new AnalyticsRateLimitAcquireResult(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);
        }

        try
        {
            AnalyticsRateLimitAcquireResult result = await EvaluateScriptAsync(resolved, policy, partitionMaterial, cancellationToken);
            CompleteSuccess(permit);
            return result;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            AbortProbe(permit);
            throw;
        }
        catch (Exception ex) when (IsInfrastructureFailure(ex))
        {
            EnterOpen(ex, policy);
            return new AnalyticsRateLimitAcquireResult(AnalyticsRateLimitAcquireStatus.UNAVAILABLE);
        }
    }

    private CallPermit TryBeginCall()
    {
        // Lock-free healthy fast path — no Redis under any circuit lock.
        if (Volatile.Read(ref _circuitState) == STATE_HEALTHY)
        {
            return CallPermit.NORMAL;
        }

        lock (_circuitLock)
        {
            if (_circuitState == STATE_HEALTHY)
            {
                return CallPermit.NORMAL;
            }

            if (_circuitState == STATE_HALF_OPEN)
            {
                return CallPermit.DENIED;
            }

            var now = timeProvider.GetUtcNow().ToUnixTimeMilliseconds();
            if (now < _openUntilTimestamp)
            {
                return CallPermit.DENIED;
            }

            _circuitState = STATE_HALF_OPEN;
            return CallPermit.PROBE;
        }
    }

    private void CompleteSuccess(CallPermit permit)
    {
        if (permit != CallPermit.PROBE)
        {
            return;
        }

        lock (_circuitLock)
        {
            // Only the owning probe may close the circuit, and only while still HALF_OPEN.
            // A concurrent EnterOpen can move HALF_OPEN → OPEN first; do not clear deadline/log.
            if (_circuitState != STATE_HALF_OPEN)
            {
                return;
            }

            _circuitState = STATE_HEALTHY;
            _openUntilTimestamp = 0;
            _outageLogged = 0;
            logger.LogInformation("Redis analytics rate limit recovered");
        }
    }

    private void AbortProbe(CallPermit permit)
    {
        if (permit != CallPermit.PROBE)
        {
            return;
        }

        lock (_circuitLock)
        {
            if (_circuitState != STATE_HALF_OPEN)
            {
                return;
            }

            _circuitState = STATE_OPEN;
            _openUntilTimestamp = timeProvider.GetUtcNow().Add(_outageBackoff).ToUnixTimeMilliseconds();
        }
    }

    private void EnterOpen(Exception ex, AnalyticsRateLimitPolicy policy)
    {
        var shouldLogWarning = false;
        lock (_circuitLock)
        {
            _openUntilTimestamp = timeProvider.GetUtcNow().Add(_outageBackoff).ToUnixTimeMilliseconds();
            _circuitState = STATE_OPEN;
            if (_outageLogged == 0)
            {
                _outageLogged = 1;
                shouldLogWarning = true;
            }
        }

        if (shouldLogWarning)
        {
            logger.LogWarning(ex, "Redis analytics rate limit unavailable for policy {Policy}", policy);
        }
    }

    private static bool IsInfrastructureFailure(Exception ex) =>
        ex is RedisException
            or TimeoutException
            or IOException
            or RedisRateLimitProtocolException;

    private AnalyticsRateLimitAcquireResult EvaluateScript(
        (int Limit, int WindowSeconds, string Domain) resolved,
        AnalyticsRateLimitPolicy policy,
        string partitionMaterial)
    {
        var redisKey = BuildRedisKey(resolved.Domain, partitionMaterial);
        RedisResult result = _database.ScriptEvaluate(
            FIXED_WINDOW_SCRIPT,
            [redisKey],
            [resolved.Limit, resolved.WindowSeconds]);

        return MapScriptResult(result, policy);
    }

    private async ValueTask<AnalyticsRateLimitAcquireResult> EvaluateScriptAsync(
        (int Limit, int WindowSeconds, string Domain) resolved,
        AnalyticsRateLimitPolicy policy,
        string partitionMaterial,
        CancellationToken cancellationToken)
    {
        var redisKey = BuildRedisKey(resolved.Domain, partitionMaterial);
        RedisResult result = await _database.ScriptEvaluateAsync(
            FIXED_WINDOW_SCRIPT,
            [redisKey],
            [resolved.Limit, resolved.WindowSeconds]);

        cancellationToken.ThrowIfCancellationRequested();
        return MapScriptResult(result, policy);
    }

    private string BuildRedisKey(string domain, string partitionMaterial)
    {
        var hashedPartition = AnalyticsRateLimitPartitionHasher.HashPartition(
            domain,
            partitionMaterial,
            _keySecret);
        return $"ab:rl:{domain}:{hashedPartition}";
    }

    private static AnalyticsRateLimitAcquireResult MapScriptResult(
        RedisResult result,
        AnalyticsRateLimitPolicy policy)
    {
        if (result.IsNull || result.Resp2Type != ResultType.Array)
        {
            throw new RedisRateLimitProtocolException(
                $"Unexpected Redis rate-limit script result type for policy {policy}.");
        }

        RedisResult[] values;
        try
        {
            values = (RedisResult[])result!;
        }
        catch (InvalidCastException ex)
        {
            throw new RedisRateLimitProtocolException(
                $"Unexpected Redis rate-limit script payload for policy {policy}.",
                ex);
        }

        if (values.Length < 2)
        {
            throw new RedisRateLimitProtocolException(
                $"Unexpected Redis rate-limit script arity for policy {policy}.");
        }

        int allowedRaw;
        int retryAfterSeconds;
        try
        {
            allowedRaw = (int)values[0];
            retryAfterSeconds = (int)values[1];
        }
        catch (Exception ex) when (ex is InvalidCastException or OverflowException or FormatException)
        {
            throw new RedisRateLimitProtocolException(
                $"Unexpected Redis rate-limit script element types for policy {policy}.",
                ex);
        }

        if (allowedRaw is not (0 or 1))
        {
            throw new RedisRateLimitProtocolException(
                $"Unexpected Redis rate-limit allowed flag '{allowedRaw}' for policy {policy}.");
        }

        if (retryAfterSeconds < 0)
        {
            throw new RedisRateLimitProtocolException(
                $"Unexpected Redis rate-limit retry-after '{retryAfterSeconds}' for policy {policy}.");
        }

        var retryAfter = TimeSpan.FromSeconds(Math.Max(1, retryAfterSeconds));
        return allowedRaw == 1
            ? new AnalyticsRateLimitAcquireResult(AnalyticsRateLimitAcquireStatus.ACQUIRED, retryAfter)
            : new AnalyticsRateLimitAcquireResult(AnalyticsRateLimitAcquireStatus.DENIED, retryAfter);
    }

    private static (int Limit, int WindowSeconds, string Domain) ResolvePolicy(AnalyticsRateLimitPolicy policy) =>
        policy switch
        {
            AnalyticsRateLimitPolicy.ANALYTICS_EVENTS => (
                RateLimitingConstants.Windows.ANALYTICS_EVENTS_LIMIT,
                RateLimitingConstants.Windows.ANALYTICS_EVENTS_PERIOD_SECONDS,
                "analytics-events"),
            AnalyticsRateLimitPolicy.SELLER_ANALYTICS_SALES_EXPORT => (
                RateLimitingConstants.Windows.SELLER_ANALYTICS_SALES_EXPORT_LIMIT,
                RateLimitingConstants.Windows.SELLER_ANALYTICS_SALES_EXPORT_PERIOD_SECONDS,
                "seller-export"),
            _ => throw new ArgumentOutOfRangeException(nameof(policy), policy, null)
        };

    private sealed class RedisRateLimitProtocolException : Exception
    {
        public RedisRateLimitProtocolException(string message) : base(message)
        {
        }

        public RedisRateLimitProtocolException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}
