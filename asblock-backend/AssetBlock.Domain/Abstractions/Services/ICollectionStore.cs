using AssetBlock.Domain.Core.Dto.Collections;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface ICollectionStore
{
    Task<Collection?> GetById(Guid id, CancellationToken cancellationToken = default);
    Task<Collection?> GetForUpdate(Guid id, CancellationToken cancellationToken = default);
    Task<CollectionDetailDto?> GetPublicDetail(Guid id, CancellationToken cancellationToken = default);
    Task<CollectionDetailDto?> GetSellerDetail(Guid id, Guid sellerId, CancellationToken cancellationToken = default);
    Task<PagedResult<CollectionListItemDto>> ListPublic(ListCollectionsRequest request, CancellationToken cancellationToken = default);
    Task<PagedResult<CollectionListItemDto>> ListForSeller(Guid sellerId, ListMyCollectionsRequest request, CancellationToken cancellationToken = default);

    Task<Collection> Create(Guid sellerId, string title, string? description, CancellationToken cancellationToken = default);
    Task UpdateMetadata(Guid id, string title, string? description, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task AddItem(Guid collectionId, Guid assetId, CancellationToken cancellationToken = default);
    Task RemoveItem(Guid collectionId, Guid assetId, CancellationToken cancellationToken = default);
    Task ReorderItems(Guid collectionId, IReadOnlyList<Guid> orderedAssetIds, CancellationToken cancellationToken = default);

    Task<bool> TryPublish(Guid id, Guid sellerId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<bool> TryArchive(Guid id, Guid sellerId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<bool> TryRestoreToDraft(Guid id, Guid sellerId, DateTimeOffset now, CancellationToken cancellationToken = default);

    Task<int> CountActiveItems(Guid collectionId, CancellationToken cancellationToken = default);
    Task<bool> OwnsActiveAsset(Guid sellerId, Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seller of a publicly visible collection, or null when the collection is not published or has no
    /// visible items. Avoids loading the detail projection when only ownership is needed.
    /// </summary>
    Task<Guid?> GetPublishedSellerId(Guid collectionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Seller of a published collection that publicly contains the asset, or null otherwise. Returns a
    /// single answer on purpose so a caller cannot tell which of the two conditions failed.
    /// </summary>
    Task<Guid?> GetPublishedMemberSellerId(
        Guid collectionId,
        Guid assetId,
        CancellationToken cancellationToken = default);
}
