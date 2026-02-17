using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Dto.Categories;
using Ardalis.Result;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Categories.GetCategories;

internal sealed class GetCategoriesQueryHandler(ICategoryStore categoryStore, ILogger<GetCategoriesQueryHandler> logger)
    : IRequestHandler<GetCategoriesQuery, Result<Domain.Dto.Paging.PagedResult<CategoryListItem>>>
{
    public async Task<Result<AssetBlock.Domain.Dto.Paging.PagedResult<CategoryListItem>>> Handle(GetCategoriesQuery request, CancellationToken cancellationToken)
    {
        var paged = await categoryStore.GetPaged(request.Request, cancellationToken);
        var items = paged.Items
            .Select(c => new CategoryListItem(c.Id, c.Name, c.Slug, c.Description))
            .ToList();
        var result = new AssetBlock.Domain.Dto.Paging.PagedResult<CategoryListItem>(items, paged.TotalCount, paged.Page, paged.PageSize);

        if (!string.IsNullOrWhiteSpace(request.Request.Search))
        {
            logger.LogDebug("Categories list Search={Search} returned {Count} of {Total}", request.Request.Search, items.Count, paged.TotalCount);
        }

        return Result.Success(result);
    }
}
