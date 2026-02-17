using AssetBlock.Domain.Abstractions.Services;

namespace AssetBlock.Infrastructure.Services;

internal sealed class DownloadService(
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    IAssetStorageService assetStorageService,
    IEncryptionService encryptionService) : IDownloadService
{
    public async Task<(Stream Content, string FileName)?> GetAssetStream(Guid assetId, Guid userId, CancellationToken cancellationToken = default)
    {
        var asset = await assetStore.GetById(assetId, cancellationToken);
        if (asset is null)
        {
            return null;
        }

        var isAuthor = asset.AuthorId == userId;
        var hasPurchase = await purchaseStore.Exists(userId, assetId, cancellationToken);
        if (!isAuthor && !hasPurchase)
        {
            return null;
        }

        await using var encryptedStream = await assetStorageService.Get(asset.StorageKey, cancellationToken);
        var plainStream = new MemoryStream();
        await encryptionService.Decrypt(encryptedStream, plainStream, cancellationToken);
        plainStream.Position = 0;
        return (plainStream, asset.FileName);
    }
}
