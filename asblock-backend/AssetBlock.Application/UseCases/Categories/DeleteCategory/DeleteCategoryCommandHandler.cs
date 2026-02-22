using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using MediatR;
using AssetBlock.Domain.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Categories.DeleteCategory;

internal sealed class DeleteCategoryCommandHandler(
    ICategoryStore categoryStore,
    ICacheService cache,
    ILogger<DeleteCategoryCommandHandler> logger)
    : IRequestHandler<DeleteCategoryCommand, Result>
{
    public async Task<Result> Handle(DeleteCategoryCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var deleted = await categoryStore.Delete(request.Id, cancellationToken);
            if (!deleted)
            {
                return Result.NotFound(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
            }
        }
        catch (CategoryInUseException)
        {
            logger.LogWarning("Cannot delete category {CategoryId}: in use by assets", request.Id);
            return Result.Error(ErrorCodes.ERR_BAD_REQUEST);
        }

        await cache.RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, cancellationToken);
        return Result.Success();
    }
}
