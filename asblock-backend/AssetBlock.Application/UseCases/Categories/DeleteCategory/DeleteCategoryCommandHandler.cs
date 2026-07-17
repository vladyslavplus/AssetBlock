using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Exceptions;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Categories.DeleteCategory;

internal sealed class DeleteCategoryCommandHandler(
    ICategoryStore categoryStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<DeleteCategoryCommandHandler> logger)
    : IRequestHandler<DeleteCategoryCommand, Result>
{
    public async Task<Result> Handle(DeleteCategoryCommand request, CancellationToken cancellationToken)
    {
        bool deleted = false;
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                deleted = await categoryStore.Delete(request.Id, ct);
                if (deleted)
                {
                    await auditWriter.Write(new AuditEvent(
                        AuditActions.CATEGORY_DELETE,
                        AuditOutcome.SUCCESS,
                        AuditResourceTypes.CATEGORY,
                        request.Id.ToString()), ct);
                }
            }, cancellationToken);
        }
        catch (CategoryInUseException)
        {
            logger.LogWarning("Cannot delete category {CategoryId}: in use by assets", request.Id);
            return ResultError.Error(ErrorCodes.ERR_BAD_REQUEST);
        }

        if (!deleted)
        {
            return Result.NotFound(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        }

        await cache.RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, cancellationToken);
        return Result.Success();
    }
}
