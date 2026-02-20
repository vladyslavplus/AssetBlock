using System.Collections.Concurrent;
using AssetBlock.Domain.Abstractions.Services;

namespace AssetBlock.Infrastructure.Services;

/// <summary>
/// In-memory fallback when Redis is not configured. Not distributed.
/// </summary>
internal sealed class MemoryCacheService : ICacheService
{
    private readonly ConcurrentDictionary<string, (string Value, DateTime? ExpiresAt)> _store = new();
    private readonly ConcurrentDictionary<string, (long Count, DateTime ExpiresAt)> _counters = new();

    public Task<string?> GetString(string key, CancellationToken cancellationToken = default)
    {
        if (_store.TryGetValue(key, out var entry) && (entry.ExpiresAt is null || entry.ExpiresAt > DateTime.UtcNow))
        {
            return Task.FromResult<string?>(entry.Value);
        }
        _store.TryRemove(key, out _);

        return Task.FromResult<string?>(null);
    }

    public Task SetString(string key, string value, TimeSpan? expiration = null, CancellationToken cancellationToken = default)
    {
        var expiresAt = expiration is null ? (DateTime?)null : DateTime.UtcNow.Add(expiration.Value);
        _store[key] = (value, expiresAt);
        return Task.CompletedTask;
    }

    public Task RemoveByPrefix(string prefix, CancellationToken cancellationToken = default)
    {
        var toRemove = _store.Keys.Where(k => k.StartsWith(prefix, StringComparison.Ordinal)).ToList();
        foreach (var key in toRemove)
        {
            _store.TryRemove(key, out _);
        }
        return Task.CompletedTask;
    }

    public Task<long> Increment(string key, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        var result = _counters.AddOrUpdate(
            key,
            _ => (1L, now.Add(expiry)),
            (_, old) => old.ExpiresAt <= now ? (1L, now.Add(expiry)) : (old.Count + 1, old.ExpiresAt));

        return Task.FromResult(result.Count);
    }
}

