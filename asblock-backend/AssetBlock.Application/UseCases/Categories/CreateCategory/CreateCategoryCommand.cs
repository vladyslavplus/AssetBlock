using Ardalis.Result;
using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.Application.Messaging;

namespace AssetBlock.Application.UseCases.Categories.CreateCategory;

public sealed record CreateCategoryCommand(string Name, string? Description, string Slug) : IRequest<Result<CreateCategoryResponse>>;
