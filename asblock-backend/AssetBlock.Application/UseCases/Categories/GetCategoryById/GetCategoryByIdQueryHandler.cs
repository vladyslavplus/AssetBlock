using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Categories;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Categories.GetCategoryById;

internal sealed class GetCategoryByIdQueryHandler(
    ICategoryStore categoryStore,
    ILogger<GetCategoryByIdQueryHandler> logger)
    : IRequestHandler<GetCategoryByIdQuery, Result<CategoryResponse>>
{
    public async Task<Result<CategoryResponse>> Handle(GetCategoryByIdQuery request, CancellationToken cancellationToken)
    {
        try
        {
            var category = await categoryStore.GetById(request.Id, cancellationToken);
            return category is null ? ResultError.Error<CategoryResponse>(ErrorCodes.ERR_CATEGORY_NOT_FOUND) : Result.Success(new CategoryResponse(category.Id, category.Name, category.Slug, category.Description));
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to get category {CategoryId}", request.Id);
            throw;
        }
    }
}
