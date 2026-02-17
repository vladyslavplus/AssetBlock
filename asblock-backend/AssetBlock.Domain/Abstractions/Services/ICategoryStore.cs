using AssetBlock.Domain.Core.Dto.Categories;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface ICategoryStore
{
    Task<Category?> GetById(Guid id, CancellationToken cancellationToken = default);
    Task<PagedResult<Category>> GetPaged(GetCategoriesRequest request, CancellationToken cancellationToken = default);
}
