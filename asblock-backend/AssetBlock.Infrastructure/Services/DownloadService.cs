using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Assets;
using AssetBlock.Domain.Core.Entities;
using AssetBlock.Domain.Core.Enums;
using AssetBlock.Domain.Core.Primitives.Api;

namespace AssetBlock.Infrastructure.Services;

internal sealed class DownloadService(
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    ICacheService cacheService) : IDownloadService
{
    public async Task<DownloadAuthorization> AuthorizeDownload(Guid assetId, Guid userId, Guid? versionId = null,
        CancellationToken cancellationToken = default)
    {
        // Load the asset for ownership check and download-rate-limit value.
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

            // Resolve the version to serve. Purchasers may access their purchased version and all higher versions.
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

        // Authors may download any version.
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
            return v is null ? null : (v.StorageKey, v.FileName);
        }

        AssetCurrentVersionSnapshot? snapshot = await assetStore.GetCurrentVersionSnapshot(assetId, cancellationToken);
        return snapshot is null ? null : (snapshot.StorageKey, snapshot.FileName);
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
            AssetVersion? requested = await assetStore.GetVersion(assetId, versionId.Value, cancellationToken);
            if (requested is null)
            {
                return null;
            }

            if (requested.VersionNumber < purchasedVersionNumber)
            {
                return VersionResolution.Forbidden;
            }

            if (requested.ProcessingStatus != AssetVersionProcessingStatus.READY)
            {
                return null;
            }

            return new VersionResolution(requested.StorageKey, requested.FileName);
        }

        // Default — serve current version if entitled.
        AssetCurrentVersionSnapshot? snapshot = await assetStore.GetCurrentVersionSnapshot(assetId, cancellationToken);
        if (snapshot is null)
        {
            return null;
        }

        if (snapshot.VersionNumber < purchasedVersionNumber)
        {
            return VersionResolution.Forbidden;
        }

        return new VersionResolution(snapshot.StorageKey, snapshot.FileName);
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
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TimeSpan expiresIn = CacheKeys.DownloadCounterExpiry(now);
        var counterKey = CacheKeys.DownloadCounter(assetId, userId, now);
        var count = await cacheService.Increment(counterKey, expiresIn, cancellationToken);

        return count > limit;
    }
}
