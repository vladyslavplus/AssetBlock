namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Distributed cache (e.g., Redis) for caching query results and other data.
/// </summary>
public interface ICacheService
{
    /// <exception cref="Core.Exceptions.CacheUnavailableException">The configured cache cannot serve reads.</exception>
    Task<string?> GetString(string key, CancellationToken cancellationToken = default);
    Task SetString(string key, string value, TimeSpan expiration, CancellationToken cancellationToken = default);
    Task Remove(string key, CancellationToken cancellationToken = default);
    Task RemoveByPrefix(string prefix, CancellationToken cancellationToken = default);

    /// <summary>Atomically increments a counter and sets expiry (only on first increment). Returns the new value.</summary>
    Task<long> Increment(string key, TimeSpan expiry, CancellationToken cancellationToken = default);
}
