using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.GetAssets;

internal sealed class GetAssetsQueryHandler(
    IAssetStore assetStore,
    ICacheService cache,
    ILogger<GetAssetsQueryHandler> logger)
    : IRequestHandler<GetAssetsQuery, Result<Domain.Core.Dto.Paging.PagedResult<AssetListItem>>>
{
    private static readonly TimeSpan _cacheExpiration = CatalogCacheConstants.ASSETS_LIST_TTL;
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<AssetListItem>>> Handle(GetAssetsQuery request, CancellationToken cancellationToken)
    {
        var normalizedRequest = request.Request with { Tags = AssetListNormalization.NormalizeTags(request.Request.Tags) };
        var key = CacheKeys.AssetsList(normalizedRequest);
        var cached = await cache.GetString(key, cancellationToken);
        if (cached is not null)
        {
            try
            {
                var cachedResult = JsonSerializer.Deserialize<Domain.Core.Dto.Paging.PagedResult<AssetListItem>>(cached, _jsonOptions);
                if (cachedResult is not null)
                {
                    logger.LogDebug("Asset list cache hit for key {Key}", key);
                    return Result.Success(AssetListNormalization.NormalizeDescriptions(cachedResult));
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid asset list cache payload for key {Key}", key);
                await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);
            }
        }

        var paged = await assetStore.GetPaged(normalizedRequest, cancellationToken);
        var normalized = AssetListNormalization.NormalizeDescriptions(paged);

        await cache.SetString(key, JsonSerializer.Serialize(normalized, _jsonOptions), _cacheExpiration, cancellationToken);
        return Result.Success(normalized);
    }
}
