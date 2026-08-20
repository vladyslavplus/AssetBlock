using Ardalis.Result;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Categories.UpdateCategory;

public sealed record UpdateCategoryCommand(Guid Id, string? Name, string? Description, string? Slug) : IRequest<Result>;
