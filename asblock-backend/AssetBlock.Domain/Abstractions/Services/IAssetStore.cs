using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IAssetStore
{
    Task<Asset> Add(Asset asset, CancellationToken cancellationToken = default);
    Task<Asset> AddWithTags(Asset asset, List<Tag> tags, CancellationToken cancellationToken = default);

    /// <summary>Inserts asset + first version (and optional tags) atomically within the caller's transaction.</summary>
    Task<Asset> AddWithVersion(Asset asset, AssetVersion version, List<Tag>? tags, CancellationToken cancellationToken = default);

    Task<Asset?> GetById(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Locks one asset row for a short lifecycle transaction.</summary>
    Task<Asset?> GetForUpdate(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Returns the current version projection used for checkout and detail views.</summary>
    Task<AssetCurrentVersionSnapshot?> GetCurrentVersionSnapshot(Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>Returns a specific version of an asset (used for version-pinned downloads).</summary>
    Task<AssetVersion?> GetVersion(Guid assetId, Guid versionId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Lists published versions visible to the requester.
    /// All versions are returned when the requester is the author or has a purchase.
    /// Only returns results when the asset is not deleted, unless <paramref name="includeDeletedAsset"/> is true.
    /// </summary>
    Task<IReadOnlyList<AssetVersionSummaryDto>> ListVersions(
        Guid assetId,
        bool includeDeletedAsset,
        Guid? requesterUserId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Within the caller's transaction: acquires a row-level lock on the asset, clears IsCurrent on all
    /// prior versions, assigns VersionNumber = max + 1, sets IsCurrent = true, inserts and returns the new version.
    /// </summary>
    Task<AssetVersion> PublishNextVersion(Guid assetId, Guid authorId, AssetVersion draft, CancellationToken cancellationToken = default);

    /// <summary>Returns all storage keys for an asset's published versions.</summary>
    Task<IReadOnlyList<string>> GetAllStorageKeys(Guid assetId, CancellationToken cancellationToken = default);

    /// <summary>Returns true when the key is referenced by any AssetVersion row.</summary>
    Task<bool> ExistsByStorageKey(string storageKey, CancellationToken cancellationToken = default);

    Task<PagedResult<AssetListItem>> GetPaged(GetAssetsRequest request, CancellationToken cancellationToken = default);
    Task SoftDelete(Guid id, DateTimeOffset deletedAt, CancellationToken cancellationToken = default);
    Task Delete(Guid id, CancellationToken cancellationToken = default);
    Task AddTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default);
    Task<bool> HasAssetTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default);
    Task<bool> RemoveTag(Guid assetId, Guid tagId, CancellationToken cancellationToken = default);
    Task<bool> Update(Guid id, string? title, string? description, decimal? price, Guid? categoryId, CancellationToken cancellationToken = default);
}
