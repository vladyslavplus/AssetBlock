using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Categories;
using MediatR;
using AssetBlock.Domain.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Categories.CreateCategory;

internal sealed class CreateCategoryCommandHandler(
    ICategoryStore categoryStore,
    ICacheService cache,
    ILogger<CreateCategoryCommandHandler> logger)
    : IRequestHandler<CreateCategoryCommand, Result<CreateCategoryResponse>>
{
    public async Task<Result<CreateCategoryResponse>> Handle(CreateCategoryCommand request, CancellationToken cancellationToken)
    {
        var slugExists = await categoryStore.SlugExists(request.Slug, null, cancellationToken);
        if (slugExists)
        {
            return ResultError.Error<CreateCategoryResponse>(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
        }

        try
        {
            var category = await categoryStore.Create(request.Name, request.Description, request.Slug, cancellationToken);
            await cache.RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, cancellationToken);
            return Result.Success(new CreateCategoryResponse(category.Id));
        }
        catch (DuplicateSlugException)
        {
            logger.LogWarning("Category slug already exists {Slug}", request.Slug);
            return ResultError.Error<CreateCategoryResponse>(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
        }
    }
}
