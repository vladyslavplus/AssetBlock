using Ardalis.Result;
using AssetBlock.Application.Messaging;
using AssetBlock.Domain.Core.Dto.Categories;

namespace AssetBlock.Application.UseCases.Categories.CreateCategory;

public sealed record CreateCategoryCommand(string Name, string? Description, string Slug) : IRequest<Result<CreateCategoryResponse>>;
