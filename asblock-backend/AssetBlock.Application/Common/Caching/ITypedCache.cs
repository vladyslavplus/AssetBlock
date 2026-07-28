namespace AssetBlock.Application.Common.Caching;

/// <summary>
/// Typed JSON cache over <see cref="Domain.Abstractions.Services.ICacheService"/>.
/// Fail-open on infrastructure errors; rethrows cancellation.
/// </summary>
public interface ITypedCache
{
    Task<T?> Get<T>(string key, CancellationToken cancellationToken = default)
        where T : class;

    Task Set<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
        where T : class;
}
