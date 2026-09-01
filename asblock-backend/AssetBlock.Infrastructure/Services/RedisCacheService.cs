using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Exceptions;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AssetBlock.Infrastructure.Services;

internal sealed class RedisCacheService(
    IConnectionMultiplexer connectionMultiplexer,
    ILogger<RedisCacheService> logger,
    TimeProvider? timeProvider = null) : ICacheService
{
    private const int READ_FAILURE_THRESHOLD = 3;
    private static readonly TimeSpan _readCircuitBreakDuration = TimeSpan.FromSeconds(30);
    private const string SET_INDEXED_VALUE_SCRIPT = """
        local expiry = tonumber(ARGV[2])
        redis.call('SET', KEYS[1], ARGV[1], 'PX', expiry)
        for index = 2, #KEYS do
            redis.call('SADD', KEYS[index], KEYS[1])
            local currentTtl = redis.call('PTTL', KEYS[index])
            if currentTtl < expiry then
                redis.call('PEXPIRE', KEYS[index], expiry)
            end
        end
        return 1
        """;
    private const string TAKE_INVALIDATION_MEMBERS_SCRIPT = """
        local members = redis.call('SMEMBERS', KEYS[1])
        redis.call('DEL', KEYS[1])
        return members
        """;

    private readonly IDatabase _db = connectionMultiplexer.GetDatabase();
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _readCircuitGate = new();
    private int _consecutiveReadFailures;
    private DateTimeOffset? _readCircuitOpenUntil;

    public async Task<string?> GetString(string key, CancellationToken cancellationToken = default)
    {
        ThrowIfReadCircuitOpen();
        try
        {
            RedisValue value = await _db.StringGetAsync(key).WaitAsync(cancellationToken);
            RecordReadSuccess();
            return value.HasValue ? value.ToString() : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            RecordReadFailure();
            logger.LogWarning(ex, "Redis GetString failed for key {Key}", key);
            throw new CacheUnavailableException("Redis cache read failed.", ex);
        }
    }

    public async Task SetString(
        string key,
        string value,
        TimeSpan expiration,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiration, TimeSpan.Zero);
        try
        {
            IReadOnlyList<string> prefixes = CacheKeys.InvalidationPrefixes(key);
            if (prefixes.Count == 0)
            {
                await _db.StringSetAsync(key, value, expiration).WaitAsync(cancellationToken);
            }
            else
            {
                RedisKey[] keys = [key, .. prefixes.Select(CacheKeys.InvalidationIndex).Select(prefix => (RedisKey)prefix)];
                RedisValue[] values = [value, checked((long)Math.Ceiling(expiration.TotalMilliseconds))];
                await _db.ScriptEvaluateAsync(SET_INDEXED_VALUE_SCRIPT, keys, values).WaitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis SetString failed for key {Key}", key);
        }
    }

    public async Task Remove(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await _db.KeyDeleteAsync(key).WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis Remove failed for key {Key}", key);
        }
    }

    public async Task RemoveByPrefix(string prefix, CancellationToken cancellationToken = default)
    {
        try
        {
            const int batchSize = 512;
            RedisResult result = await _db.ScriptEvaluateAsync(
                    TAKE_INVALIDATION_MEMBERS_SCRIPT,
                    [CacheKeys.InvalidationIndex(prefix)],
                    [])
                .WaitAsync(cancellationToken);
            if (result.IsNull)
            {
                return;
            }

            var members = (RedisResult[])result!;
            for (var offset = 0; offset < members.Length; offset += batchSize)
            {
                RedisKey[] batch = members
                    .Skip(offset)
                    .Take(batchSize)
                    .Select(member => (string?)member)
                    .Where(member => !string.IsNullOrEmpty(member))
                    .Select(member => (RedisKey)member!)
                    .ToArray();
                if (batch.Length > 0)
                {
                    await _db.KeyDeleteAsync(batch).WaitAsync(cancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis RemoveByPrefix failed for prefix {Prefix}", prefix);
        }
    }

    public async Task<long> Increment(string key, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        try
        {
            var count = await _db.StringIncrementAsync(key).WaitAsync(cancellationToken);
            TimeSpan? ttl = await _db.KeyTimeToLiveAsync(key).WaitAsync(cancellationToken);
            if (count == 1 || ttl is null)
            {
                await _db.KeyExpireAsync(key, expiry).WaitAsync(cancellationToken);
            }

            return count;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis IncrementAsync failed for key {Key}", key);
            throw;
        }
    }

    private void ThrowIfReadCircuitOpen()
    {
        lock (_readCircuitGate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (_readCircuitOpenUntil is { } openUntil && openUntil > now)
            {
                throw new CacheUnavailableException("Redis cache read circuit is open.");
            }

            if (_readCircuitOpenUntil is not null)
            {
                _readCircuitOpenUntil = null;
                _consecutiveReadFailures = 0;
            }
        }
    }

    private void RecordReadSuccess()
    {
        lock (_readCircuitGate)
        {
            _consecutiveReadFailures = 0;
            _readCircuitOpenUntil = null;
        }
    }

    private void RecordReadFailure()
    {
        lock (_readCircuitGate)
        {
            _consecutiveReadFailures++;
            if (_consecutiveReadFailures >= READ_FAILURE_THRESHOLD)
            {
                _readCircuitOpenUntil = _timeProvider.GetUtcNow().Add(_readCircuitBreakDuration);
            }
        }
    }
}
