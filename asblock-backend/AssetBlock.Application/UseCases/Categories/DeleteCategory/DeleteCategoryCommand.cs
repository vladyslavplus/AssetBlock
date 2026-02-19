using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Categories.DeleteCategory;

public sealed record DeleteCategoryCommand(Guid Id) : IRequest<Result>;
