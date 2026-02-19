using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Categories;
using MediatR;

namespace AssetBlock.Application.UseCases.Categories.CreateCategory;

internal sealed class CreateCategoryCommandHandler(ICategoryStore categoryStore, ICacheService cache)
    : IRequestHandler<CreateCategoryCommand, Result<CreateCategoryResponse>>
{
    public async Task<Result<CreateCategoryResponse>> Handle(CreateCategoryCommand request, CancellationToken cancellationToken)
    {
        var slugExists = await categoryStore.SlugExists(request.Slug, null, cancellationToken);
        if (slugExists)
        {
            return ResultError.Error<CreateCategoryResponse>(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
        }

        var category = await categoryStore.Create(request.Name, request.Description, request.Slug, cancellationToken);
        await cache.RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, cancellationToken);

        return Result.Success(new CreateCategoryResponse(category.Id));
    }
}
