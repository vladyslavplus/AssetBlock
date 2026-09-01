namespace AssetBlock.Application.Common.Caching;

/// <summary>
/// Typed JSON cache over <see cref="Domain.Abstractions.Services.ICacheService"/>.
/// Fail-open on payload errors; cache unavailability and cancellation propagate.
/// </summary>
public interface ITypedCache
{
    Task<T?> Get<T>(string key, CancellationToken cancellationToken = default)
        where T : class;

    Task Set<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
        where T : class;
}
