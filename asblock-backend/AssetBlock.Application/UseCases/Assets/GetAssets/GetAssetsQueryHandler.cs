using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Application.Common.Caching;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.GetAssets;

internal sealed class GetAssetsQueryHandler(
    IAssetStore assetStore,
    ITypedCache cache,
    ILogger<GetAssetsQueryHandler> logger)
    : IRequestHandler<GetAssetsQuery, Result<Domain.Core.Dto.Paging.PagedResult<AssetListItem>>>
{
    private static readonly TimeSpan _cacheExpiration = CatalogCacheConstants.ASSETS_LIST_TTL;

    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<AssetListItem>>> Handle(GetAssetsQuery request, CancellationToken cancellationToken)
    {
        GetAssetsRequest normalizedRequest = request.Request with { Tags = AssetListNormalization.NormalizeTags(request.Request.Tags) };
        var key = CacheKeys.AssetsList(normalizedRequest);
        Domain.Core.Dto.Paging.PagedResult<AssetListItem>? cached = await cache.Get<Domain.Core.Dto.Paging.PagedResult<AssetListItem>>(key, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Asset list cache hit for key {Key}", key);
            return Result.Success(AssetListNormalization.NormalizeDescriptions(cached));
        }

        Domain.Core.Dto.Paging.PagedResult<AssetListItem> paged = await assetStore.GetPaged(normalizedRequest, cancellationToken);
        Domain.Core.Dto.Paging.PagedResult<AssetListItem> normalized = AssetListNormalization.NormalizeDescriptions(paged);

        await cache.Set(key, normalized, _cacheExpiration, cancellationToken);
        return Result.Success(normalized);
    }
}
