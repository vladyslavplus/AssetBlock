using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.UpdateAsset;

internal sealed class UpdateAssetCommandHandler(
    IAssetStore assetStore,
    ICategoryStore categoryStore,
    IUnitOfWork unitOfWork,
    IOutboxStore outboxStore,
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

            if (asset.DeletedAt.HasValue)
            {
                return Result.NotFound(ErrorCodes.ERR_ASSET_NOT_FOUND);
            }

            if (request.CategoryId.HasValue)
            {
                var category = await categoryStore.GetById(request.CategoryId.Value, cancellationToken);
                if (category is null)
                {
                    return Result.NotFound(ErrorCodes.ERR_CATEGORY_NOT_FOUND);
                }
            }

            await unitOfWork.ExecuteInTransaction(async ct =>
            {
                var updated = await assetStore.Update(
                    request.AssetId,
                    request.Title,
                    request.Description,
                    request.Price,
                    request.CategoryId,
                    ct);

                if (!updated)
                {
                    throw new InvalidOperationException("Asset update returned false after load.");
                }

                await outboxStore.Enqueue(
                    OutboxMessageTypes.ASSET_INDEX_UPSERT,
                    new AssetIndexUpsertPayload(request.AssetId),
                    ct);
            }, cancellationToken);

            try
            {
                await cache.RemoveByPrefix(CacheKeys.ASSETS_LIST_PREFIX, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Cache invalidation failed after asset update {AssetId}", request.AssetId);
            }

            logger.LogInformation("Updated asset: {AssetId}", request.AssetId);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to update asset: {AssetId}", request.AssetId);
            return Result.Error(ErrorCodes.ERR_INTERNAL);
        }
    }
}
