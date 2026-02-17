using AssetBlock.Domain.Dto.Categories;
using AssetBlock.Domain.Dto.Paging;
using AssetBlock.Domain.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface ICategoryStore
{
    Task<Category?> GetById(Guid id, CancellationToken cancellationToken = default);
    Task<PagedResult<Category>> GetPaged(GetCategoriesRequest request, CancellationToken cancellationToken = default);
}
