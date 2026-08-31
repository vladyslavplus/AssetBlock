using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Categories;

namespace AssetBlock.Application.UseCases.Categories.GetCategories;

public sealed record GetCategoriesQuery(GetCategoriesRequest Request) : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<CategoryListItem>>>;
