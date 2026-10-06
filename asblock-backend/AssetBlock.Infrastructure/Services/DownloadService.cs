using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Primitives.Api;
using AssetBlock.Infrastructure.Persistence.Publication;

namespace AssetBlock.Infrastructure.Services;

internal sealed class DownloadService(
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    ICacheService cacheService,
    TimeProvider? timeProvider = null) : IDownloadService
{
    private readonly TimeProvider _timeProvider = timeProvider ?? TimeProvider.System;

    public async Task<DownloadAuthorization> AuthorizeDownload(Guid assetId, Guid userId, Guid? versionId = null,
        CancellationToken cancellationToken = default)
    {
        Asset? asset = await assetStore.GetById(assetId, includeDeleted: true, cancellationToken);
        if (asset is null)
        {
            return new DownloadAuthorization(AssetDownloadStatus.NOT_FOUND);
        }

        var isAuthor = asset.AuthorId == userId;

        if (!isAuthor)
        {
            Purchase? purchase = await purchaseStore.GetPurchase(userId, assetId, cancellationToken);
            if (purchase is null)
            {
                return new DownloadAuthorization(AssetDownloadStatus.FORBIDDEN);
            }

            VersionResolution? targetVersion = await ResolveEntitledVersion(assetId, versionId, purchase, cancellationToken);
            if (targetVersion is null)
            {
                return new DownloadAuthorization(AssetDownloadStatus.NOT_FOUND);
            }

            if (targetVersion.Denied)
            {
                return new DownloadAuthorization(AssetDownloadStatus.FORBIDDEN);
            }

            if (asset.DownloadLimitPerHour.HasValue &&
                await IsRateLimited(assetId, userId, asset.DownloadLimitPerHour.Value, cancellationToken))
            {
                return new DownloadAuthorization(AssetDownloadStatus.RATE_LIMITED);
            }

            return new DownloadAuthorization(AssetDownloadStatus.SUCCESS, new DownloadPermit(targetVersion.StorageKey!, targetVersion.FileName!));
        }

        (string StorageKey, string FileName)? authorVersion = await ResolveAuthorVersion(assetId, versionId, cancellationToken);
        if (authorVersion is null)
        {
            return new DownloadAuthorization(AssetDownloadStatus.NOT_FOUND);
        }

        if (asset.DownloadLimitPerHour.HasValue &&
            await IsRateLimited(assetId, userId, asset.DownloadLimitPerHour.Value, cancellationToken))
        {
            return new DownloadAuthorization(AssetDownloadStatus.RATE_LIMITED);
        }

        return new DownloadAuthorization(AssetDownloadStatus.SUCCESS, new DownloadPermit(authorVersion.Value.StorageKey, authorVersion.Value.FileName));
    }

    private async Task<(string StorageKey, string FileName)?> ResolveAuthorVersion(
        Guid assetId,
        Guid? versionId,
        CancellationToken cancellationToken)
    {
        if (versionId.HasValue)
        {
            AssetVersion? v = await assetStore.GetVersion(assetId, versionId.Value, cancellationToken);
            if (v is null || !PublicationEligibilityQuery.IsAuthorSafePreviewVersion(v))
            {
                return null;
            }

            return (v.StorageKey, v.FileName);
        }

        AssetCurrentVersionSnapshot? snapshot = await assetStore.GetCurrentVersionSnapshot(assetId, cancellationToken);
        if (snapshot is not null)
        {
            return (snapshot.StorageKey, snapshot.FileName);
        }

        AssetVersion? highestApproved = await assetStore.GetHighestEntitledApprovedVersion(assetId, 1, cancellationToken);
        if (highestApproved is not null)
        {
            return (highestApproved.StorageKey, highestApproved.FileName);
        }

        return null;
    }

    private async Task<VersionResolution?> ResolveEntitledVersion(
        Guid assetId,
        Guid? versionId,
        Purchase purchase,
        CancellationToken cancellationToken)
    {
        AssetVersion? purchasedVersion = await assetStore.GetVersion(assetId, purchase.AssetVersionId, cancellationToken);
        if (purchasedVersion is null)
        {
            return null;
        }

        var purchasedVersionNumber = purchasedVersion.VersionNumber;

        if (versionId.HasValue)
        {
            if (versionId.Value == purchase.AssetVersionId)
            {
                if (!await assetStore.IsExactVersionSafeForDownload(assetId, versionId.Value, cancellationToken))
                {
                    return null;
                }

                return new VersionResolution(purchasedVersion.StorageKey, purchasedVersion.FileName);
            }

            AssetVersion? requested = await assetStore.GetVersion(assetId, versionId.Value, cancellationToken);
            if (requested is null)
            {
                return null;
            }

            if (requested.VersionNumber < purchasedVersionNumber)
            {
                return VersionResolution.Forbidden;
            }

            if (!await assetStore.IsApprovedBuyerDownloadVersion(
                    assetId,
                    versionId.Value,
                    purchasedVersionNumber,
                    cancellationToken))
            {
                return null;
            }

            return new VersionResolution(requested.StorageKey, requested.FileName);
        }

        AssetVersion? highestApproved = await assetStore.GetHighestEntitledApprovedVersion(
            assetId,
            purchasedVersionNumber,
            cancellationToken);
        if (highestApproved is not null)
        {
            return new VersionResolution(highestApproved.StorageKey, highestApproved.FileName);
        }

        if (await assetStore.IsExactVersionSafeForDownload(assetId, purchase.AssetVersionId, cancellationToken))
        {
            return new VersionResolution(purchasedVersion.StorageKey, purchasedVersion.FileName);
        }

        return null;
    }

    private sealed class VersionResolution(string? storageKey, string? fileName)
    {
        public static readonly VersionResolution Forbidden = new(null, null) { Denied = true };

        public string? StorageKey { get; } = storageKey;
        public string? FileName { get; } = fileName;
        public bool Denied { get; private init; }
    }

    public async Task CopyDecrypted(string storageKey, Stream destination, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageKey);
        ArgumentNullException.ThrowIfNull(destination);

        await assetStorageService.OpenRead(
            storageKey,
            async (encryptedStream, ct) =>
                await encryptionService.Decrypt(encryptedStream, destination, ct).ConfigureAwait(false),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task<bool> IsRateLimited(Guid assetId, Guid userId, int limit, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();
        TimeSpan expiresIn = CacheKeys.DownloadCounterExpiry(now);
        var counterKey = CacheKeys.DownloadCounter(assetId, userId, now);
        var count = await cacheService.Increment(counterKey, expiresIn, cancellationToken);

        return count > limit;
    }
}
