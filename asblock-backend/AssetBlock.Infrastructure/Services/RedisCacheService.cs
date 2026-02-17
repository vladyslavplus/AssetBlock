using AssetBlock.Domain.Abstractions.Services;
using StackExchange.Redis;
using Microsoft.Extensions.Logging;

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
            var value = await _db.StringGetAsync(key).WaitAsync(cancellationToken);
            return value.HasValue ? value.ToString() : null;
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
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis SetString failed for key {Key}", key);
        }
    }

    public async Task RemoveByPrefix(string prefix, CancellationToken cancellationToken = default)
    {
        try
        {
            var server = connectionMultiplexer.GetServer(connectionMultiplexer.GetEndPoints().First());
            var keys = server.Keys(pattern: prefix + "*").ToArray();
            if (keys.Length > 0)
            {
                await _db.KeyDeleteAsync(keys).WaitAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Redis RemoveByPrefix failed for prefix {Prefix}", prefix);
        }
    }
}
