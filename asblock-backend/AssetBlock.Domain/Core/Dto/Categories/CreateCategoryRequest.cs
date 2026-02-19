namespace AssetBlock.Domain.Core.Dto.Categories;

public sealed record CreateCategoryRequest(string Name, string? Description, string Slug);
