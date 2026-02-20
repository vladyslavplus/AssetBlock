using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Categories;
using MediatR;

namespace AssetBlock.Application.UseCases.Categories.GetCategoryById;

public sealed record GetCategoryByIdQuery(Guid Id) : IRequest<Result<CategoryResponse>>;
