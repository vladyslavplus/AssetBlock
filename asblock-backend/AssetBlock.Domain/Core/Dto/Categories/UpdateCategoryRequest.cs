namespace AssetBlock.Domain.Core.Dto.Categories;

public sealed record UpdateCategoryRequest(string? Name, string? Description, string? Slug);
