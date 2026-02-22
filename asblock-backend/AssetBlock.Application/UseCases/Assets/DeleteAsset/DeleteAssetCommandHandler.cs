using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.DeleteAsset;

internal sealed class DeleteAssetCommandHandler(
    IAssetStore assetStore,
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

        try
        {
            await storageService.Delete(asset.StorageKey, cancellationToken);
            await searchService.DeleteAsset(asset.Id, cancellationToken);
            await assetStore.Delete(asset.Id, cancellationToken);

            await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);

            logger.LogInformation("Deleted asset: {AssetId}", request.Id);
            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete asset: {AssetId}. Operation may be partially complete.", request.Id);
            return Result.Error(ErrorCodes.ERR_BAD_REQUEST);
        }
    }
}
