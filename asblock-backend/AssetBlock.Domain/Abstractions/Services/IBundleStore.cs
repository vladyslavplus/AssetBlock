using AssetBlock.Domain.Core.Dto.Bundles;
using AssetBlock.Domain.Core.Dto.Paging;
using AssetBlock.Domain.Core.Entities;

namespace AssetBlock.Domain.Abstractions.Services;

public interface IBundleStore
{
    Task<Bundle?> GetById(Guid id, CancellationToken cancellationToken = default);
    Task<Bundle?> LockForUpdate(Guid id, CancellationToken cancellationToken = default);

    Task<BundleDetailDto?> GetPublicDetail(Guid id, CancellationToken cancellationToken = default);
    Task<BundleDetailDto?> GetSellerDetail(Guid id, Guid sellerId, CancellationToken cancellationToken = default);
    Task<PagedResult<BundleListItemDto>> ListPublic(ListBundlesRequest request, CancellationToken cancellationToken = default);
    Task<PagedResult<BundleListItemDto>> ListForSeller(Guid sellerId, ListMyBundlesRequest request, CancellationToken cancellationToken = default);

    /// <summary>Creates a bundle with revision 1 and marks it current.</summary>
    Task<(Bundle Bundle, BundleRevision Revision)> CreateWithRevision(
        Guid sellerId,
        string title,
        string? description,
        decimal price,
        string currency,
        decimal listPriceTotal,
        IReadOnlyList<BundleRevisionItemDraft> items,
        CancellationToken cancellationToken = default);

    /// <summary>Appends the next immutable revision and flips IsCurrent under the bundle row lock.</summary>
    Task<BundleRevision> PublishNextRevision(
        Guid bundleId,
        string title,
        string? description,
        decimal price,
        string currency,
        decimal listPriceTotal,
        IReadOnlyList<BundleRevisionItemDraft> items,
        CancellationToken cancellationToken = default);

    Task<bool> TryArchive(Guid id, Guid sellerId, DateTimeOffset now, CancellationToken cancellationToken = default);
    Task<bool> TryRestore(Guid id, Guid sellerId, DateTimeOffset now, CancellationToken cancellationToken = default);

    /// <summary>Current revision with items for checkout preparation. Null if missing/archived/unavailable.</summary>
    Task<BundleCheckoutSnapshot?> GetCheckoutSnapshot(Guid bundleId, CancellationToken cancellationToken = default);

    Task LockAssetsInOrder(IReadOnlyList<Guid> assetIds, CancellationToken cancellationToken = default);

    /// <summary>Returns SellerId when the bundle is publicly listable, otherwise null.</summary>
    Task<Guid?> GetPublicAnalyticsSellerId(Guid bundleId, CancellationToken cancellationToken = default);
}

/// <summary>Draft item used when creating a bundle revision.</summary>
public sealed record BundleRevisionItemDraft(
    Guid AssetId,
    int Position,
    string AssetTitleSnapshot,
    decimal ListPriceSnapshot);
