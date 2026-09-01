using AssetBlock.Domain.Abstractions.Services;

namespace AssetBlock.Infrastructure.Services;

/// <summary>Bounded in-memory fallback when Redis is not configured. Not distributed.</summary>
internal sealed class MemoryCacheService(
    TimeProvider? timeProvider = null,
    int maxEntries = MemoryCacheService.DEFAULT_MAX_ENTRIES) : ICacheService
{
    private const int DEFAULT_MAX_ENTRIES = 10_000;

    private readonly Lock _gate = new();
    private readonly Dictionary<string, CacheEntry> _store = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CounterEntry> _counters = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly int _maxEntries = maxEntries > 0
        ? maxEntries
        : throw new ArgumentOutOfRangeException(nameof(maxEntries));
    private long _sequence;

    public Task<string?> GetString(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (_store.TryGetValue(key, out CacheEntry? entry) && entry.ExpiresAt > now)
            {
                return Task.FromResult<string?>(entry.Value);
            }

            _store.Remove(key);
            return Task.FromResult<string?>(null);
        }
    }

    public Task SetString(string key, string value, TimeSpan expiration, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiration, TimeSpan.Zero);
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (!_store.ContainsKey(key))
            {
                EnsureCapacity(_store, now, static entry => entry.ExpiresAt, static entry => entry.Sequence);
            }

            _store[key] = new CacheEntry(value, now.Add(expiration), ++_sequence);
        }

        return Task.CompletedTask;
    }

    public Task Remove(string key, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            _store.Remove(key);
        }

        return Task.CompletedTask;
    }

    public Task RemoveByPrefix(string prefix, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            foreach (var key in _store.Keys.Where(key => key.StartsWith(prefix, StringComparison.Ordinal)).ToArray())
            {
                _store.Remove(key);
            }
        }

        return Task.CompletedTask;
    }

    public Task<long> Increment(string key, TimeSpan expiry, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiry, TimeSpan.Zero);
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (_counters.TryGetValue(key, out CounterEntry? existing) && existing.ExpiresAt > now)
            {
                CounterEntry updated = existing with { Count = existing.Count + 1 };
                _counters[key] = updated;
                return Task.FromResult(updated.Count);
            }

            if (!_counters.ContainsKey(key))
            {
                EnsureCapacity(_counters, now, static entry => entry.ExpiresAt, static entry => entry.Sequence);
            }

            _counters[key] = new CounterEntry(1, now.Add(expiry), ++_sequence);
            return Task.FromResult(1L);
        }
    }

    private void EnsureCapacity<TEntry>(
        Dictionary<string, TEntry> entries,
        DateTimeOffset now,
        Func<TEntry, DateTimeOffset> expiresAt,
        Func<TEntry, long> sequence)
    {
        foreach (var expiredKey in entries
                     .Where(pair => expiresAt(pair.Value) <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            entries.Remove(expiredKey);
        }

        if (entries.Count < _maxEntries)
        {
            return;
        }

        var oldestKey = entries.MinBy(pair => sequence(pair.Value)).Key;
        entries.Remove(oldestKey);
    }

    private sealed record CacheEntry(string Value, DateTimeOffset ExpiresAt, long Sequence);
    private sealed record CounterEntry(long Count, DateTimeOffset ExpiresAt, long Sequence);
}

