using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAssetStore
{
    Task<Asset> Add(Asset asset, CancellationToken cancellationToken = default);
    Task<Asset> AddWithTags(Asset asset, List<Tag> tags, CancellationToken cancellationToken = default);
    Task<Asset?> GetById(Guid id, CancellationToken cancellationToken = default);
    Task<PagedResult<Asset>> GetPaged(GetAssetsRequest request, CancellationToken cancellationToken = default);
    Task Delete(Guid id, CancellationToken cancellationToken = default);
    Task AddTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default);
    Task<bool> HasAssetTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default);
    Task<bool> RemoveTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default);
    Task<bool> Update(Guid id, string? title, string? description, decimal? price, Guid? categoryId, CancellationToken cancellationToken = default);
}
