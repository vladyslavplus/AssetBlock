using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Primitives.Api;

namespace AssetBlock.Infrastructure.Services;

internal sealed class DownloadService(
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService,
    ICacheService cacheService) : IDownloadService
{
    private const string DOWNLOAD_COUNTER_PREFIX = "dl";

    public async Task<AssetDownloadResult> GetAssetStream(Guid assetId, Guid userId,
        CancellationToken cancellationToken = default)
    {
        var asset = await assetStore.GetById(assetId, cancellationToken);
        if (asset is null)
        {
            return new AssetDownloadResult(AssetDownloadStatus.NotFound, null, null);
        }

        var isAuthor = asset.AuthorId == userId;
        var hasPurchase = await purchaseStore.Exists(userId, assetId, cancellationToken);
        if (!isAuthor && !hasPurchase)
        {
            return new AssetDownloadResult(AssetDownloadStatus.Forbidden, null, null);
        }

        // Download quota check (per-user, per-asset, per-hour)
        if (asset.DownloadLimitPerHour.HasValue)
        {
            var hour = DateTimeOffset.UtcNow.Hour;
            var counterKey = $"{DOWNLOAD_COUNTER_PREFIX}:{assetId}:{userId}:{hour}";
            var count = await cacheService.Increment(counterKey, TimeSpan.FromHours(1), cancellationToken);
            if (count > asset.DownloadLimitPerHour.Value)
            {
                return new AssetDownloadResult(AssetDownloadStatus.RateLimited, null, null);
            }
        }

        await using var encryptedStream = await assetStorageService.Get(asset.StorageKey, cancellationToken);
        var tempPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None,
                             4096, FileOptions.Asynchronous))
            {
                await encryptionService.Decrypt(encryptedStream, fileStream, cancellationToken);
            }

            var content = new FileStream(tempPath, FileMode.Open, FileAccess.Read, FileShare.Read, 4096,
                FileOptions.Asynchronous | FileOptions.DeleteOnClose);
            return new AssetDownloadResult(AssetDownloadStatus.Success, content, asset.FileName);
        }
        catch
        {
            if (File.Exists(tempPath))
            {
                try { File.Delete(tempPath); }
                catch
                {
                    /* ignore */
                }
            }

            throw;
        }
    }
}
