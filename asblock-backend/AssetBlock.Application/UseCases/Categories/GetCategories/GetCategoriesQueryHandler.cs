using System.Text.Json;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Categories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Categories.GetCategories;

internal sealed class GetCategoriesQueryHandler(
    ICategoryStore categoryStore,
    ICacheService cache,
    ILogger<GetCategoriesQueryHandler> logger)
    : IRequestHandler<GetCategoriesQuery, Result<Domain.Core.Dto.Paging.PagedResult<CategoryListItem>>>
{
    private static readonly TimeSpan _cacheExpiration = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions _jsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<CategoryListItem>>> Handle(GetCategoriesQuery request, CancellationToken cancellationToken)
    {
        var key = CacheKeys.CategoriesList(request.Request);
        var cached = await cache.GetString(key, cancellationToken);
        if (cached is not null)
        {
            try
            {
                var cachedResult = JsonSerializer.Deserialize<Domain.Core.Dto.Paging.PagedResult<CategoryListItem>>(cached, _jsonOptions);
                if (cachedResult is not null)
                {
                    logger.LogDebug("Categories list cache hit for key {Key}", key);
                    return Result.Success(cachedResult);
                }
            }
            catch (JsonException ex)
            {
                logger.LogWarning(ex, "Invalid categories list cache payload for key {Key}", key);
                await cache.RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, cancellationToken);
            }
        }

        var paged = await categoryStore.GetPaged(request.Request, cancellationToken);
        var items = paged.Items
            .Select(c => new CategoryListItem(c.Id, c.Name, c.Slug, c.Description))
            .ToList();
        var result = new Domain.Core.Dto.Paging.PagedResult<CategoryListItem>(items, paged.TotalCount, paged.Page, paged.PageSize);

        await cache.SetString(key, JsonSerializer.Serialize(result, _jsonOptions), _cacheExpiration, cancellationToken);
        return Result.Success(result);
    }
}
