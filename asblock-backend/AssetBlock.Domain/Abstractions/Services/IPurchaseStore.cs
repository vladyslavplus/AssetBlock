using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Dto.Users;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IPurchaseStore
{
    Task<Purchase> Add(Purchase purchase, CancellationToken cancellationToken = default);

    Task<bool> HasPurchasesForAsset(Guid assetId, CancellationToken cancellationToken = default);

    Task<bool> Exists(Guid userId, Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>Returns true when the user owns any of the given assets.</summary>
    Task<bool> ExistsAny(Guid userId, IReadOnlyList<Guid> assetIds, CancellationToken cancellationToken = default);

    Task<Purchase?> GetPurchase(Guid userId, Guid assetId, CancellationToken cancellationToken = default);

    Task<PagedResult<PurchaseLibraryItemDto>> ListForUser(
        Guid userId,
        ListMyPurchasesRequest request,
        CancellationToken cancellationToken = default);
}
