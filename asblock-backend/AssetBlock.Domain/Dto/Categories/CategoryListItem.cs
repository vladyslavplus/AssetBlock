namespace AssetBlock.Domain.Dto.Categories;

public sealed record CategoryListItem(Guid Id, string Name, string Slug, string? Description);
