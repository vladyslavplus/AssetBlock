using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.Common.Caching;

internal sealed class JsonTypedCache(
    ICacheService cache,
    ILogger<JsonTypedCache> logger) : ITypedCache
{
    private static readonly JsonSerializerOptions _jsonOptions =
        new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<T?> Get<T>(string key, CancellationToken cancellationToken = default)
        where T : class
    {
        string? cached;
        try
        {
            cached = await cache.GetString(key, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to read cache key {Key}", key);
            return null;
        }

        if (cached is null)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(cached, _jsonOptions);
        }
        catch (Exception ex) when (ex is JsonException or NotSupportedException)
        {
            logger.LogWarning(ex, "Invalid cache payload for key {Key}; removing", key);
            try
            {
                await cache.Remove(key, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception removeEx)
            {
                logger.LogWarning(removeEx, "Failed to remove invalid cache key {Key}", key);
            }

            return null;
        }
    }

    public async Task Set<T>(string key, T value, TimeSpan expiration, CancellationToken cancellationToken = default)
        where T : class
    {
        try
        {
            var json = JsonSerializer.Serialize(value, _jsonOptions);
            await cache.SetString(key, json, expiration, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to cache key {Key}", key);
        }
    }
}
