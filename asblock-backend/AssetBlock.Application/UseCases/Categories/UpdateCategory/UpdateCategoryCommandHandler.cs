using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Audit;
using AssetBlock.Domain.Core.Enums;
using MediatR;
using AssetBlock.Domain.Core.Exceptions;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Categories.UpdateCategory;

internal sealed class UpdateCategoryCommandHandler(
    ICategoryStore categoryStore,
    IUnitOfWork unitOfWork,
    IAuditWriter auditWriter,
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
                return Result.NotFound(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
            }

            if (request.Slug is not null && request.Slug != category.Slug)
            {
                var slugExists = await categoryStore.SlugExists(request.Slug, request.Id, cancellationToken);
                if (slugExists)
                {
                    return Result.Conflict(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
                }
            }

            var changedFields = new List<string>();
            if (request.Name is not null)
            {
                category.Name = request.Name;
                changedFields.Add("name");
            }

            if (request.Description is not null)
            {
                category.Description = request.Description;
                changedFields.Add("description");
            }

            if (request.Slug is not null)
            {
                category.Slug = request.Slug;
                changedFields.Add("slug");
            }

            category.UpdatedAt = DateTimeOffset.UtcNow;

            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                await categoryStore.Update(category, ct);
                await auditWriter.Write(new AuditEvent(
                    AuditActions.CATEGORY_UPDATE,
                    AuditOutcome.SUCCESS,
                    AuditResourceTypes.CATEGORY,
                    request.Id.ToString(),
                    new Dictionary<string, object?> { ["changedFields"] = changedFields }), ct);
            }, cancellationToken);

            await cache.RemoveByPrefix(CacheKeys.CATEGORIES_LIST_PREFIX, cancellationToken);
            return Result.Success();
        }
        catch (DuplicateSlugException)
        {
            logger.LogWarning("Category slug already exists {Slug}", request.Slug);
            return Result.Conflict(ErrorCodes.ERR_CATEGORY_SLUG_EXISTS);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update category {CategoryId}", request.Id);
            throw;
        }
    }
}
