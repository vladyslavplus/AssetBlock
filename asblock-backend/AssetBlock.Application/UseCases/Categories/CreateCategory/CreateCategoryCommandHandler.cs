using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.Domain.Core.Enums;
using MediatR;
using AssetBlock.Domain.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Categories.CreateCategory;

internal sealed class CreateCategoryCommandHandler(
    ICategoryStore categoryStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
    ICacheService cache,
    ILogger<CreateCategoryCommandHandler> logger)
    : IRequestHandler<CreateCategoryCommand, Result<CreateCategoryResponse>>
{
    public async Task<Result<CreateCategoryResponse>> Handle(CreateCategoryCommand request, CancellationToken cancellationToken)
    {
        var slugExists = await categoryStore.SlugExists(request.Slug, null, cancellationToken);
        if (slugExists)
        {
            return Result.Conflict(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
        }

        Guid categoryId = Guid.Empty;
        try
        {
            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                var category = await categoryStore.Create(request.Name, request.Description, request.Slug, ct);
                categoryId = category.Id;
                await auditWriter.Write(new AuditEvent(
                    AuditActions.CATEGORY_CREATE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.CATEGORY,
                    category.Id.ToString()), ct);
            }, cancellationToken);
        }
        catch (DuplicateSlugException)
        {
            logger.LogWarning("Category slug already exists {Slug}", request.Slug);
            return Result.Conflict(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
        }

        await cache.RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, cancellationToken);
        return Result.Success(new CreateCategoryResponse(categoryId));
    }
}
