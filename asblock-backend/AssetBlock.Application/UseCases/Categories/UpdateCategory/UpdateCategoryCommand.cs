using Ardalis.Result;
using MediatR;

namespace AssetBlock.Application.UseCases.Categories.UpdateCategory;

public sealed record UpdateCategoryCommand(Guid Id, string? Name, string? Description, string? Slug) : IRequest<Result>;
