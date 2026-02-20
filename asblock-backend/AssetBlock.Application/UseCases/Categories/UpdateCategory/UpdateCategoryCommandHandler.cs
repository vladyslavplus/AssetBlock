using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using MediatR;
using AssetBlock.Domain.Core.Exceptions;

using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Categories.UpdateCategory;

internal sealed class UpdateCategoryCommandHandler(
    ICategoryStore categoryStore,
    ICacheService cache,
    ILogger<UpdateCategoryCommandHandler> logger)
    : IRequestHandler<UpdateCategoryCommand, Result>
{
    public async Task<Result> Handle(UpdateCategoryCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var category = await categoryStore.GetById(request.Id, cancellationToken);
            if (category is null)
            {
                return ResultError.Error(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
            }

            if (request.Slug is not null && request.Slug != category.Slug)
            {
                var slugExists = await categoryStore.SlugExists(request.Slug, request.Id, cancellationToken);
                if (slugExists)
                {
                    return ResultError.Error(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
                }
            }

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
        catch (DuplicateSlugException)
        {
            logger.LogWarning("Category slug already exists {Slug}", request.Slug);
            return ResultError.Error(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update category {CategoryId}", request.Id);
            throw;
        }
    }
}
