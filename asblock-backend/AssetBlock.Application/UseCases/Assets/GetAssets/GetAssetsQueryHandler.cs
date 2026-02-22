using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using Ardalis.Result;
using MediatR;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.GetAssets;

internal sealed class GetAssetsQueryHandler(
    IAssetSearchService searchService,
    ICacheService cache,
    ILogger<GetAssetsQueryHandler> logger)
    : IRequestHandler<GetAssetsQuery, Result<Domain.Core.Dto.Paging.PagedResult<AssetListItem>>>
{
    private static readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(2);
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<AssetListItem>>> Handle(GetAssetsQuery request, CancellationToken cancellationToken)
    {
        var normalizedRequest = request.Request with { Tags = NormalizeTags(request.Request.Tags) };
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
                    return Result.Success(cachedResult);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid asset list cache payload for key {Key}", key);
                await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);
            }
        }

        var paged = await searchService.SearchAssets(normalizedRequest, cancellationToken);
        var items = paged.Items
            .Select(a => new AssetListItem(
                a.Id,
                a.Title,
                a.Description,
                a.Price,
                a.CategoryId,
                a.CategoryName,
                a.AuthorId,
                a.AuthorUsername,
                a.CreatedAt,
                a.Tags,
                a.AverageRating))
            .ToList();
        var result = new Domain.Core.Dto.Paging.PagedResult<AssetListItem>(items, paged.TotalCount, paged.Page, paged.PageSize);

        await cache.SetString(key, JsonSerializer.Serialize(result, _jsonOptions), _cacheExpiration, cancellationToken);
        return Result.Success(result);
    }

    private static List<string>? NormalizeTags(IReadOnlyList<string>? tags)
    {
        if (tags is null || tags.Count == 0)
        {
            return null;
        }
        var list = tags
            .SelectMany(t => t.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(t => t.Trim().ToLowerInvariant())
            .Where(t => t.Length > 0)
            .Distinct()
            .ToList();
        return list.Count > 0 ? list : null;
    }
}
