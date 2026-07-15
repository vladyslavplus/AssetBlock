using Ardalis.Result;
using AssetBlock.Domain.Abstractions.Services;
using AssetBlock.Domain.Core.Constants;
using AssetBlock.Domain.Core.Dto.Outbox;
using MediatR;
using Microsoft.Extensions.Logging;

namespace AssetBlock.Application.UseCases.Assets.DeleteAsset;

internal sealed class DeleteAssetCommandHandler(
    IAssetStore assetStore,
    IPurchaseStore purchaseStore,
    IUnitOfWork unitOfWork,
    IOutboxStore outboxStore,
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
                logger.LogWarning(ex, "Cache invalidation failed for already-deleted asset {AssetId}", request.Id);
            }

            logger.LogInformation("Delete idempotent: asset already delisted {AssetId}", request.Id);
            return Result.Success();
        }

        var hasPurchases = await purchaseStore.HasPurchasesForAsset(request.Id, cancellationToken);
        var storageKey = asset.StorageKey;

        try
        {
            if (hasPurchases)
            {
                await assetStore.SoftDelete(asset.Id, DateTimeOffset.UtcNow, cancellationToken);
            }
            else
            {
                await unitOfWork.ExecuteInTransaction(async ct =>
                {
                    await assetStore.Delete(asset.Id, ct);
                    await outboxStore.Enqueue(
                        OutboxMessageTypes.ASSET_BLOB_DELETE,
                        new AssetBlobDeletePayload(asset.Id, storageKey),
                        ct);
                }, cancellationToken);
            }

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
                logger.LogWarning(ex, "Cache invalidation failed after delete {AssetId}", request.Id);
            }

            logger.LogInformation(
                hasPurchases
                    ? "Soft-deleted (delisted) asset {AssetId}: purchases exist; blob retained."
                    : "Hard-deleted asset {AssetId}; blob delete enqueued.",
                request.Id);
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to delete asset: {AssetId}.", request.Id);
            return Result.Error(ErrorCodes.ERR_INTERNAL);
        }
    }
}
