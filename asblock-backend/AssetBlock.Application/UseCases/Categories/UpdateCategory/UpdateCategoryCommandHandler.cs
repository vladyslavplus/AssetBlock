using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using MediatR;

namespace AssetBlock.Application.UseCases.Categories.UpdateCategory;

internal sealed class UpdateCategoryCommandHandler(ICategoryStore categoryStore, ICacheService cache)
    : IRequestHandler<UpdateCategoryCommand, Result>
{
    public async Task<Result> Handle(UpdateCategoryCommand request, CancellationToken cancellationToken)
    {
        var category = await categoryStore.GetById(request.Id, cancellationToken);
        if (category is null)
        {
            return ResultError.Error(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        }

        // Slug uniqueness check — only when a new slug is provided and differs from current
        if (request.Slug is not null && request.Slug != category.Slug)
        {
            var slugExists = await categoryStore.SlugExists(request.Slug, request.Id, cancellationToken);
            if (slugExists)
            {
                return ResultError.Error(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
            }
        }

        // Apply only the fields that were explicitly provided (partial update / PATCH semantics)
        if (request.Name is not null)
        {
            category.Name = request.Name;
        }

        if (request.Description is not null)
        {
            category.Description = request.Description;
        }

        if (request.Slug is not null)
        {
            category.Slug = request.Slug;
        }

        category.UpdatedAt = DateTimeOffset.UtcNow;

        await categoryStore.Update(category, cancellationToken);
        await cache.RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, cancellationToken);

        return Result.Success();
    }
}
