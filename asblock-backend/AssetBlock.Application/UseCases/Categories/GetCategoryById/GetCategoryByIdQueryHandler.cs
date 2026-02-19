using Ardalis.Result;
using AssetBlock.Application.Common;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Categories;
using MediatR;

namespace AssetBlock.Application.UseCases.Categories.GetCategoryById;

internal sealed class GetCategoryByIdQueryHandler(ICategoryStore categoryStore)
    : IRequestHandler<GetCategoryByIdQuery, Result<CategoryResponse>>
{
    public async Task<Result<CategoryResponse>> Handle(GetCategoryByIdQuery request, CancellationToken cancellationToken)
    {
        var category = await categoryStore.GetById(request.Id, cancellationToken);
        if (category is null)
        {
            return ResultError.Error<CategoryResponse>(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
        }

        return Result.Success(new CategoryResponse(category.Id, category.Name, category.Slug, category.Description));
    }
}
