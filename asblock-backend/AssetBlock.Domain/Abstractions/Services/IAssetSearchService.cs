using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAssetSearchService
{
    Task IndexAsset(AssetDocument document, CancellationToken cancellationToken = default);
    Task<PagedResult<AssetDocument>> SearchAssets(GetAssetsRequest request, CancellationToken cancellationToken = default);
    Task DeleteAsset(Guid id, CancellationToken cancellationToken = default);
}
