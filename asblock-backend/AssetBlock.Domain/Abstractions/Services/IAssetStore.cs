using AssetBlock.Domain.Dto.Assets;
using AssetBlock.Domain.Dto.Paging;
using AssetBlock.Domain.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAssetStore
{
    Task<Asset> Add(Asset asset, CancellationToken cancellationToken = default);
    Task<Asset?> GetById(Guid id, CancellationToken cancellationToken = default);
    Task<PagedResult<Asset>> GetPaged(GetAssetsRequest request, CancellationToken cancellationToken = default);
}
