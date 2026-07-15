using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.Api;
using System.Globalization;

namespace AssetBlock.Infrastructure.Services;

internal sealed class DownloadService(
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    ICacheService cacheService) : IDownloadService
{
    private const string DOWNLOAD_COUNTER_PREFIX = "dl";
    private const string DOWNLOAD_WINDOW_KEY_FORMAT = "yyyyMMddHH";
    private static readonly TimeSpan _downloadWindow = TimeSpan.FromHours(1);

    public async Task<DownloadAuthorization> AuthorizeDownload(Guid assetId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        var asset = await assetStore.GetById(assetId, cancellationToken);
        if (asset is null)
        {
            return new DownloadAuthorization(AssetDownloadStatus.NOT_FOUND);
        }

        var isAuthor = asset.AuthorId == userId;
        if (!isAuthor)
        {
            var hasPurchase = await purchaseStore.Exists(userId, assetId, cancellationToken);
            if (!hasPurchase)
            {
                return new DownloadAuthorization(AssetDownloadStatus.FORBIDDEN);
            }
        }

        if (asset.DownloadLimitPerHour.HasValue &&
            await IsRateLimited(assetId, userId, asset.DownloadLimitPerHour.Value, cancellationToken))
        {
            return new DownloadAuthorization(AssetDownloadStatus.RATE_LIMITED);
        }

        return new DownloadAuthorization(
            AssetDownloadStatus.SUCCESS,
            new DownloadPermit(asset.StorageKey, asset.FileName));
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
        var now = DateTimeOffset.UtcNow;
        var windowKey = now.ToString(DOWNLOAD_WINDOW_KEY_FORMAT, CultureInfo.InvariantCulture);
        var expiresIn = _downloadWindow
            - TimeSpan.FromMinutes(now.Minute)
            - TimeSpan.FromSeconds(now.Second)
            - TimeSpan.FromMilliseconds(now.Millisecond);
        var counterKey = $"{DOWNLOAD_COUNTER_PREFIX}:{assetId}:{userId}:{windowKey}";
        var count = await cacheService.Increment(counterKey, expiresIn, cancellationToken);

        return count > limit;
    }
}
