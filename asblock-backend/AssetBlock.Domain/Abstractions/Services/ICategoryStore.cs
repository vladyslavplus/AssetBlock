using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface ICategoryStore
{
    Task<Category?> GetById(Guid id, CancellationToken cancellationToken = default);
    Task<PagedResult<Category>> GetPaged(GetCategoriesRequest request, CancellationToken cancellationToken = default);
    Task<bool> SlugExists(string slug, Guid? excludeId, CancellationToken cancellationToken = default);
    Task<Category> Create(string name, string? description, string slug, CancellationToken cancellationToken = default);
    Task Update(Category category, CancellationToken cancellationToken = default);
    Task<bool> Delete(Guid id, CancellationToken cancellationToken = default);
}
