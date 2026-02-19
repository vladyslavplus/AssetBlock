using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using MediatR;

namespace AssetBlock.Application.UseCases.Categories.DeleteCategory;

internal sealed class DeleteCategoryCommandHandler(ICategoryStore categoryStore, ICacheService cache)
    : IRequestHandler<DeleteCategoryCommand, Result>
{
    public async Task<Result> Handle(DeleteCategoryCommand request, CancellationToken cancellationToken)
    {
        var deleted = await categoryStore.Delete(request.Id, cancellationToken);
        if (!deleted)
        {
            return ResultError.Error(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        }

        await cache.RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, cancellationToken);
        return Result.Success();
    }
}
