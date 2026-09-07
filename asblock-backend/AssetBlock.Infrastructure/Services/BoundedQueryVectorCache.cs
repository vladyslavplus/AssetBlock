using AssetBlock.Domain.Abstractions.Services;

namespace AssetBlock.Infrastructure.Services;

/// <summary>
/// Thread-safe process-local bounded LRU in-memory cache for query vectors.
/// Prevents unbounded memory consumption and is never stored in distributed cache (Redis).
/// </summary>
public sealed class BoundedQueryVectorCache(
    TimeProvider? timeProvider = null,
    int maxEntries = BoundedQueryVectorCache.DEFAULT_MAX_ENTRIES)
    : IQueryVectorCache
{
    private const int DEFAULT_MAX_ENTRIES = 1_000;

    private readonly int _maxEntries = maxEntries > 0
        ? maxEntries
        : throw new ArgumentOutOfRangeException(nameof(maxEntries));
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, VectorCacheEntry> _store = new(StringComparer.Ordinal);
    private long _sequence;

    public int Count
    {
        get
        {
            lock (_gate)
            {
                return _store.Count;
            }
        }
    }

    public float[]? Get(string modelKey, string searchHash)
    {
        var key = BuildKey(modelKey, searchHash);
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (_store.TryGetValue(key, out VectorCacheEntry? entry))
            {
                if (entry.ExpiresAt > now)
                {
                    entry.Sequence = ++_sequence;
                    return (float[])entry.Vector.Clone();
                }

                _store.Remove(key);
            }

            return null;
        }
    }

    public void Set(string modelKey, string searchHash, float[] vector, TimeSpan expiration)
    {
        ArgumentNullException.ThrowIfNull(vector);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(expiration, TimeSpan.Zero);

        var key = BuildKey(modelKey, searchHash);
        lock (_gate)
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();
            if (!_store.ContainsKey(key))
            {
                EnsureCapacity(now);
            }

            _store[key] = new VectorCacheEntry((float[])vector.Clone(), now.Add(expiration), ++_sequence);
        }
    }

    private void EnsureCapacity(DateTimeOffset now)
    {
        foreach (var expiredKey in _store
                     .Where(pair => pair.Value.ExpiresAt <= now)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _store.Remove(expiredKey);
        }

        if (_store.Count < _maxEntries)
        {
            return;
        }

        var oldestKey = _store.MinBy(pair => pair.Value.Sequence).Key;
        _store.Remove(oldestKey);
    }

    private static string BuildKey(string modelKey, string searchHash) =>
        $"{modelKey}:{searchHash}";

    private sealed class VectorCacheEntry(float[] vector, DateTimeOffset expiresAt, long sequence)
    {
        public float[] Vector { get; } = vector;
        public DateTimeOffset ExpiresAt { get; } = expiresAt;
        public long Sequence { get; set; } = sequence;
    }
}
