using AssetBlock.Application.Common.Caching;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.Application.Messaging;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Categories.GetCategories;

internal sealed class GetCategoriesQueryHandler(
    ICategoryStore categoryStore,
    ITypedCache cache,
    ILogger<GetCategoriesQueryHandler> logger)
    : IRequestHandler<GetCategoriesQuery, Result<Domain.Core.Dto.Paging.PagedResult<CategoryListItem>>>
{
    private static readonly TimeSpan _cacheExpiration = CatalogCacheConstants.CATEGORIES_LIST_TTL;

    public async Task<Result<Domain.Core.Dto.Paging.PagedResult<CategoryListItem>>> Handle(GetCategoriesQuery request, CancellationToken cancellationToken)
    {
        var key = CacheKeys.CategoriesList(request.Request);
        var cached = await cache.Get<Domain.Core.Dto.Paging.PagedResult<CategoryListItem>>(key, cancellationToken);
        if (cached is not null)
        {
            logger.LogDebug("Categories list cache hit for key {Key}", key);
            return Result.Success(cached);
        }

        logger.LogDebug("Categories list cache miss for key {Key}", key);
        var paged = await categoryStore.GetPaged(request.Request, cancellationToken);
        var items = paged.Items
            .Select(c => new CategoryListItem(c.Id, c.Name, c.Slug, c.Description))
            .ToList();
        var result = new Domain.Core.Dto.Paging.PagedResult<CategoryListItem>(items, paged.TotalCount, paged.Page, paged.PageSize);

        await cache.Set(key, result, _cacheExpiration, cancellationToken);
        return Result.Success(result);
    }
}
