namespace AssetBlock.Domain.Abstractions.Services;

/// <summary>
/// Distributed cache (e.g., Redis) for caching query results and other data.
/// </summary>
public interface ICacheService
{
    Task<string?> GetString(string key, CancellationToken cancellationToken = default);
    Task SetString(string key, string value, TimeSpan? expiration = null, CancellationToken cancellationToken = default);
}
