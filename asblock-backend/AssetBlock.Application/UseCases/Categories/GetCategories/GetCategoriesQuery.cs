using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Categories;
using MediatR;

namespace AssetBlock.Application.UseCases.Categories.GetCategories;

public sealed record GetCategoriesQuery(GetCategoriesRequest Request) : IRequest<Result<Domain.Core.Dto.Paging.PagedResult<CategoryListItem>>>;
