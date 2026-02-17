using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Dto.Assets;
using Ardalis.Result;
using MediatR;
using AssetBlock.Domain.Core.Constants;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.GetAssets;

internal sealed class GetAssetsQueryHandler(
    IAssetStore assetStore,
    ICacheService cache,
    ILogger<GetAssetsQueryHandler> logger)
    : IRequestHandler<GetAssetsQuery, Result<AssetBlock.Domain.Dto.Paging.PagedResult<AssetListItem>>>
{
    private static readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<Result<AssetBlock.Domain.Dto.Paging.PagedResult<AssetListItem>>> Handle(GetAssetsQuery request, CancellationToken cancellationToken)
    {
        var key = CacheKeys.AssetsList(request.Request);
        var cached = await cache.GetString(key, cancellationToken);
        if (cached is not null)
        {
            var cachedResult = JsonSerializer.Deserialize<AssetBlock.Domain.Dto.Paging.PagedResult<AssetListItem>>(cached, _jsonOptions);
            if (cachedResult is not null)
            {
                logger.LogDebug("Asset list cache hit for key {Key}", key);
                return Result.Success(cachedResult);
            }
        }

        var paged = await assetStore.GetPaged(request.Request, cancellationToken);
        var items = paged.Items
            .Select(a => new AssetListItem(
                a.Id,
                a.Title,
                a.Description,
                a.Price,
                a.CategoryId,
                a.Category.Name,
                a.AuthorId,
                a.CreatedAt))
            .ToList();
        var result = new AssetBlock.Domain.Dto.Paging.PagedResult<AssetListItem>(items, paged.TotalCount, paged.Page, paged.PageSize);

        await cache.SetString(key, JsonSerializer.Serialize(result, _jsonOptions), _cacheExpiration, cancellationToken);
        return Result.Success(result);
    }
}
