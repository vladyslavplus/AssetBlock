using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.DeleteAsset;

internal sealed class DeleteAssetCommandHandler(
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    IAssetSearchService searchService,
    IAssetStorageService storageService,
    ICacheService cache,
    ILogger<DeleteAssetCommandHandler> logger) : IRequestHandler<DeleteAssetCommand, Result>
{
    public async Task<Result> Handle(DeleteAssetCommand request, CancellationToken cancellationToken)
    {
        var asset = await assetStore.GetById(request.Id, cancellationToken);
        if (asset is null)
        {
            return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
        }

        if (asset.AuthorId != request.UserId)
        {
            return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
        }

        if (asset.DeletedAt.HasValue)
        {
            await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);
            logger.LogInformation("Delete idempotent: asset already delisted {AssetId}", request.Id);
            return Result.Success();
        }

        var hasPurchases = await purchaseStore.HasPurchasesForAsset(request.Id, cancellationToken);

        try
        {
            // Remove from catalog index first so list/search stay consistent.
            await searchService.DeleteAsset(asset.Id, cancellationToken);

            if (hasPurchases)
            {
                await assetStore.SoftDelete(asset.Id, DateTimeOffset.UtcNow, cancellationToken);
                logger.LogInformation("Soft-deleted (delisted) asset {AssetId}: purchases exist; storage object retained.", request.Id);
            }
            else
            {
                await storageService.Delete(asset.StorageKey, cancellationToken);
                await assetStore.Delete(asset.Id, cancellationToken);
                logger.LogInformation("Hard-deleted asset {AssetId}", request.Id);
            }

            await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);
            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete asset: {AssetId}. Operation may be partially complete.", request.Id);
            return Result.Error(ErrorCodes.ERR_INTERNAL);
        }
    }
}
