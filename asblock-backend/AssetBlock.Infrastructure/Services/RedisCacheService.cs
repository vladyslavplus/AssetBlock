using AssetBlock.Domain.Abstractions.Services;
using StackExchange.Redis;

namespace AssetBlock.Infrastructure.Services;

internal sealed class RedisCacheService(IConnectionMultiplexer connectionMultiplexer) : ICacheService
{
    private readonly IDatabase _db = connectionMultiplexer.GetDatabase();

    public async Task<string?> GetString(string key, CancellationToken cancellationToken = default)
    {
        var value = await _db.StringGetAsync(key);
        return value.HasValue ? value.ToString() : null;
    }

    public async Task SetString(string key, string value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        if (expiration is { } exp)
        {
            await _db.StringSetAsync(key, value, exp);
        }
        else
        {
            await _db.StringSetAsync(key, value);
        }
    }
}
