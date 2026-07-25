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
}
