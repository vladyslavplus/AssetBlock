using AssetBlock.Domain.Dto.Categories;
using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Categories.GetCategories;

public sealed record GetCategoriesQuery(GetCategoriesRequest Request) : IRequest<Result<Domain.Dto.Paging.PagedResult<CategoryListItem>>>;
