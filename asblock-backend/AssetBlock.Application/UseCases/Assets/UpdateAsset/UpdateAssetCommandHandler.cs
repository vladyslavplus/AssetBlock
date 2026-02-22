using Ardalis.Result;
using AssetBlock.Application.UseCases.Assets.Events;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.UpdateAsset;

internal sealed class UpdateAssetCommandHandler(
    IAssetStore assetStore,
    ICategoryStore categoryStore,
    IPublisher publisher,
    ICacheService cache,
    ILogger<UpdateAssetCommandHandler> logger) : IRequestHandler<UpdateAssetCommand, Result>
{
    public async Task<Result> Handle(UpdateAssetCommand request, CancellationToken cancellationToken)
    {
        try
        {
            var asset = await assetStore.GetById(request.AssetId, cancellationToken);
            if (asset is null)
            {
                return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
            }

            if (asset.AuthorId != request.UserId)
            {
                return Result.Forbidden(ErrorCodes.ERR_FORBIDDEN);
            }

            if (request.CategoryId.HasValue)
            {
                var category = await categoryStore.GetById(request.CategoryId.Value, cancellationToken);
                if (category is null)
                {
                    return Result.NotFound(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
                }
            }

            var updated = await assetStore.Update(
                request.AssetId,
                request.Title,
                request.Description,
                request.Price,
                request.CategoryId,
                cancellationToken);

            if (!updated)
            {
                return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
            }

            await publisher.Publish(new AssetCreatedEvent(request.AssetId), cancellationToken);
            await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);

            logger.LogInformation("Updated asset: {AssetId}", request.AssetId);
            return Result.Success();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update asset: {AssetId}", request.AssetId);
            return Result.Invalid(new List<ValidationError> { new(ErrorCodes.ERR_BAD_REQUEST, ErrorCodesToErrorMessages.GetMessage(ErrorCodes.ERR_BAD_REQUEST)) });
        }
    }
}
