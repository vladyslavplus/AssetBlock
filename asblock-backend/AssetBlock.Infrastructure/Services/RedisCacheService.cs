using System.Net;
using AssetBlock.Domain.Abstractions.Services;
using Microsoft.Extensions.Logging;
using StackExchange.Redis;

namespace AssetBlock.Infrastructure.Services;

internal sealed class RedisCacheService(
    IConnectionMultiplexer connectionMultiplexer,
    ILogger<RedisCacheService> logger) : ICacheService
{
    private readonly IDatabase _db = connectionMultiplexer.GetDatabase();

    public async Task<string?> GetString(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            RedisValue value = await _db.StringGetAsync(key).WaitAsync(cancellationToken);
            return value.HasValue ? value.ToString() : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis GetString failed for key {Key}", key);
            return null;
        }
    }

    public async Task SetString(string key, string value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        try
        {
            if (expiration is { } exp)
            {
                await _db.StringSetAsync(key, value, exp).WaitAsync(cancellationToken);
            }
            else
            {
                await _db.StringSetAsync(key, value).WaitAsync(cancellationToken);
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
            foreach (EndPoint endpoint in connectionMultiplexer.GetEndPoints())
            {
                IServer server = connectionMultiplexer.GetServer(endpoint);
                var batch = new List<RedisKey>(batchSize);
                foreach (RedisKey key in server.Keys(pattern: prefix + "*"))
                {
                    batch.Add(key);
                    if (batch.Count == batchSize)
                    {
                        await _db.KeyDeleteAsync(batch.ToArray()).WaitAsync(cancellationToken);
                        batch.Clear();
                    }
                }

                if (batch.Count > 0)
                {
                    await _db.KeyDeleteAsync(batch.ToArray()).WaitAsync(cancellationToken);
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
}
